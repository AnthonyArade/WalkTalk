"""WalkTalk voice training.

Adapts a Whisper model to one person's voice: reads the model WalkTalk already uses (a GGML file),
trains small low-rank adapters (LoRA) on that person's recordings, and writes the result back as a
GGML file WalkTalk can load.

A full training first holds some sentences back to find how many rounds help, then trains again for
that many rounds on every recording, and gives the user's own sentences extra practice until they
come out right. An update adds a few new sentences to a model that was already trained this way.

Usage: python train.py job.json
Progress is printed as one JSON object per line.
"""
import json
import math
import random
import re
import struct
import sys
import time
import wave

import numpy as np


def emit(**event):
    print(json.dumps(event), flush=True)


# ---------------------------------------------------------------------------------------------------
# GGML files: a header (sizes, mel filters, vocabulary) followed by the tensors, one after another.

GGML_MAGIC = 0x67676D6C
FLOAT_TYPES = {0: np.float32, 1: np.float16}


def read_ggml(path):
    """Returns (model sizes, length of the header in bytes, [(name, dims as stored, ftype, array)])."""
    with open(path, "rb") as f:
        (magic,) = struct.unpack("<i", f.read(4))
        if magic != GGML_MAGIC:
            raise ValueError("This is not a Whisper model file.")
        sizes = struct.unpack("<11i", f.read(44))
        n_mel, n_fft = struct.unpack("<2i", f.read(8))
        f.seek(n_mel * n_fft * 4, 1)
        (n_tokens,) = struct.unpack("<i", f.read(4))
        for _ in range(n_tokens):
            (length,) = struct.unpack("<i", f.read(4))
            f.seek(length, 1)
        header_bytes = f.tell()

        tensors = []
        while True:
            head = f.read(12)
            if len(head) < 12:
                break
            n_dims, name_len, ftype = struct.unpack("<3i", head)
            dims = struct.unpack(f"<{n_dims}i", f.read(4 * n_dims))
            name = f.read(name_len).decode("utf-8")
            if ftype not in FLOAT_TYPES:
                raise ValueError("Compressed (quantized) models can't be trained.")
            dtype = np.dtype(FLOAT_TYPES[ftype])
            count = int(np.prod(dims))
            data = np.frombuffer(f.read(count * dtype.itemsize), dtype=dtype)
            tensors.append((name, dims, ftype, data.reshape(dims[::-1])))
    return sizes, header_bytes, tensors


def write_ggml(base_path, out_path, header_bytes, tensors, weights):
    """Same file as the base model, tensor for tensor, with the trained weights in place of the old ones."""
    with open(base_path, "rb") as src, open(out_path, "wb") as out:
        out.write(src.read(header_bytes))
        for name, dims, ftype, old in tensors:
            data = weights[name].astype(FLOAT_TYPES[ftype]).reshape(old.shape)
            encoded = name.encode("utf-8")
            out.write(struct.pack("<3i", len(dims), len(encoded), ftype))
            out.write(struct.pack(f"<{len(dims)}i", *dims))
            out.write(encoded)
            out.write(np.ascontiguousarray(data).tobytes())


# ---------------------------------------------------------------------------------------------------

def read_wav(path):
    with wave.open(path, "rb") as w:
        if w.getframerate() != 16000 or w.getnchannels() != 1 or w.getsampwidth() != 2:
            raise ValueError(f"{path}: expected 16 kHz, mono, 16-bit audio.")
        pcm = w.readframes(w.getnframes())
    return np.frombuffer(pcm, dtype=np.int16).astype(np.float32) / 32768.0


def main(job_path):
    with open(job_path, "r", encoding="utf-8-sig") as f:
        job = json.load(f)

    emit(stage="loading")
    import torch
    import torch.nn as nn
    import torch.nn.functional as F
    from torch.utils.checkpoint import checkpoint
    import whisper
    from whisper.model import ModelDimensions, Whisper
    from whisper.tokenizer import get_tokenizer

    if not torch.cuda.is_available():
        raise RuntimeError("Voice training needs an NVIDIA graphics card with up-to-date drivers.")
    device = torch.device("cuda")
    seed = int(job.get("seed", 7))
    random.seed(seed)
    np.random.seed(seed)
    torch.manual_seed(seed)

    # -- the model WalkTalk uses, rebuilt in PyTorch from its GGML file
    sizes, header_bytes, tensors = read_ggml(job["base"])
    n_vocab, n_audio_ctx, n_audio_state, n_audio_head, n_audio_layer, \
        n_text_ctx, n_text_state, n_text_head, n_text_layer, n_mels, _ = sizes
    model = Whisper(ModelDimensions(n_mels, n_audio_ctx, n_audio_state, n_audio_head, n_audio_layer,
                                    n_vocab, n_text_ctx, n_text_state, n_text_head, n_text_layer))
    expected = model.state_dict()
    state = {}
    for name, _, _, data in tensors:
        if name not in expected:
            raise ValueError(f"Unexpected part in the model file: {name}")
        state[name] = torch.from_numpy(data.astype(np.float32)).reshape(expected[name].shape)
    model.load_state_dict(state, strict=True)
    model.to(device)
    for p in model.parameters():
        p.requires_grad_(False)

    if job.get("roundtrip"):  # self-test: writing the untouched model must reproduce the base file
        write_ggml(job["base"], job["out"], header_bytes, tensors,
                   {k: v.cpu().numpy() for k, v in model.state_dict().items()})
        emit(stage="done", roundtrip=True)
        return

    multilingual = n_vocab >= 51865
    if not multilingual:
        raise ValueError("English-only models are not supported.")
    tokenizer = get_tokenizer(True, num_languages=n_vocab - 51765 - 1, language=job["language"], task="transcribe")
    prefix = list(tokenizer.sot_sequence_including_notimestamps)

    # -- the recordings
    def load(items):
        samples = []
        for item in items:
            audio = read_wav(item["audio"])[: whisper.audio.N_SAMPLES]
            tokens = prefix + tokenizer.encode(" " + item["text"].strip()) + [tokenizer.eot]
            samples.append((torch.from_numpy(audio), tokens[: n_text_ctx // 2], item["text"]))
        return samples

    train, holdout = load(job["train"]), load(job["holdout"])
    if not train or not holdout:
        raise ValueError("Not enough recordings to train on.")
    # An update of an already trained model: "focus" holds the newly recorded sentences it has to learn.
    focus = load(job.get("focus", []))

    def mel_of(audio, augment):
        audio = audio.to(device)
        if augment:
            # A slightly faster or slower voice, and a little room noise: variety from few recordings.
            rate = random.uniform(0.94, 1.06)
            length = max(1600, int(audio.shape[0] / rate))
            audio = F.interpolate(audio[None, None], size=length, mode="linear", align_corners=False)[0, 0]
            audio = audio[: whisper.audio.N_SAMPLES]
            level = audio.pow(2).mean().sqrt().clamp_min(1e-4)
            audio = audio + torch.randn_like(audio) * level * 10 ** (-random.uniform(25, 40) / 20)
        frames = audio.shape[0] // whisper.audio.HOP_LENGTH
        mel = whisper.log_mel_spectrogram(whisper.pad_or_trim(audio), n_mels)
        if augment:
            for _ in range(2):  # hide a narrow band of pitch and a short moment
                width = random.randint(0, max(1, n_mels // 12))
                start = random.randint(0, n_mels - width)
                mel[start:start + width, :] = 0
                width = random.randint(0, 20)
                start = random.randint(0, max(1, frames - width))
                mel[:, start:start + width] = 0
        return mel

    def batch_of(samples, augment):
        mels = torch.stack([mel_of(audio, augment) for audio, _, _ in samples])
        longest = max(len(tokens) for _, tokens, _ in samples)
        inputs = torch.full((len(samples), longest - 1), tokenizer.eot, dtype=torch.long)
        targets = torch.full((len(samples), longest - 1), -100, dtype=torch.long)
        for i, (_, tokens, _) in enumerate(samples):
            inputs[i, : len(tokens) - 1] = torch.tensor(tokens[:-1])
            targets[i, len(prefix) - 1: len(tokens) - 1] = torch.tensor(tokens[len(prefix):])
        return mels, inputs.to(device), targets.to(device)

    # -- adapters: a small trainable correction added to the attention layers, the rest stays frozen
    rank, alpha = int(job.get("rank", 8)), float(job.get("alpha", 16))

    class LoRALinear(nn.Module):
        def __init__(self, base):
            super().__init__()
            self.base = base
            self.a = nn.Parameter(torch.randn(rank, base.in_features, device=device) / math.sqrt(base.in_features))
            self.b = nn.Parameter(torch.zeros(base.out_features, rank, device=device))
            self.dropout = nn.Dropout(0.05)

        def forward(self, x):
            delta = F.linear(F.linear(self.dropout(x), self.a.to(x.dtype)), self.b.to(x.dtype))
            return self.base(x) + delta * (alpha / rank)

        def merged(self):
            return self.base.weight + (self.b @ self.a) * (alpha / rank)

    adapters = {}
    parts = job.get("parts", ["query", "value", "out"])

    def adapt(prefix_name, attention):
        for part in parts:
            wrapped = LoRALinear(getattr(attention, part))
            setattr(attention, part, wrapped)
            adapters[f"{prefix_name}.{part}.weight"] = wrapped

    for i, block in enumerate(model.encoder.blocks):
        adapt(f"encoder.blocks.{i}.attn", block.attn)
    for i, block in enumerate(model.decoder.blocks):
        adapt(f"decoder.blocks.{i}.attn", block.attn)
        adapt(f"decoder.blocks.{i}.cross_attn", block.cross_attn)
    trainable = [p for a in adapters.values() for p in (a.a, a.b)]

    def encode(mels, with_grad):
        encoder = model.encoder
        x = F.gelu(encoder.conv1(mels))
        x = F.gelu(encoder.conv2(x)).permute(0, 2, 1)
        x = (x + encoder.positional_embedding).to(x.dtype)
        for block in encoder.blocks:
            # Checkpointing keeps only one layer's working memory at a time: it is what lets the large
            # models train on an 8 GB card.
            x = checkpoint(block, x, use_reentrant=False) if with_grad else block(x)
        return encoder.ln_post(x)

    def loss_of(samples, augment, with_grad):
        mels, inputs, targets = batch_of(samples, augment)
        with torch.autocast("cuda", dtype=torch.float16):
            logits = model.decoder(inputs, encode(mels, with_grad))
        return F.cross_entropy(logits.float().reshape(-1, logits.shape[-1]), targets.reshape(-1),
                               ignore_index=-100, reduction="sum"), int((targets != -100).sum())

    batch_size = int(job.get("batch", 4))

    def clean_loss(samples):
        model.eval()
        total, count = 0.0, 0
        with torch.no_grad():
            for i in range(0, len(samples), batch_size):
                loss, n = loss_of(samples[i:i + batch_size], augment=False, with_grad=False)
                total += float(loss)
                count += n
        return total / max(count, 1)

    def transcripts(samples=None):
        model.eval()
        options = whisper.DecodingOptions(language=job["language"], without_timestamps=True, fp16=True)
        with torch.no_grad():
            return [whisper.decode(model, mel_of(audio, False), options).text.strip()
                    for audio, _, _ in (holdout if samples is None else samples)]

    def snapshot():
        return [p.detach().clone() for p in trainable]

    emit(stage="baseline")
    base_loss = clean_loss(holdout)
    base_focus = clean_loss(focus) if focus else None

    # -- training
    epochs = int(job.get("epochs", 6))
    steps_per_epoch = math.ceil(len(train) / batch_size)
    total_steps = epochs * steps_per_epoch

    def new_run(planned_steps):
        # The learning rate warms up, then eases off over the planned length of the training.
        warm = max(1, planned_steps // 10)
        opt = torch.optim.AdamW(trainable, lr=float(job.get("lr", 3e-4)), weight_decay=0.01)
        sched = torch.optim.lr_scheduler.LambdaLR(opt, lambda s: min(
            (s + 1) / warm, max(0.05, 0.5 * (1 + math.cos(math.pi * (s - warm) / max(1, planned_steps - warm))))))
        return opt, sched, torch.amp.GradScaler("cuda")

    def train_on(samples, run):
        opt, sched, scale = run
        loss, n = loss_of(samples, augment=True, with_grad=True)
        opt.zero_grad(set_to_none=True)
        scale.scale(loss / n).backward()
        scale.unscale_(opt)
        torch.nn.utils.clip_grad_norm_(trainable, 1.0)
        scale.step(opt)
        scale.update()
        sched.step()
        return float(loss.detach()) / n

    def words(text):
        return re.findall(r"[^\W_]+(?:'[^\W_]+)*", text.lower())

    def written_right(samples):
        # Whether the model now writes each sentence the way the user wants it (case and punctuation aside).
        return [words(wrote) == words(text) for wrote, (_, _, text) in zip(transcripts(samples), samples)]

    def learn(lesson, targets, guard, guard_limit, rounds, phase=0):
        """Practises `targets` (they are in `lesson`, mixed with sentences already known) until the model
        writes them right, and stops as soon as it starts to unlearn `guard`. A fixed, short number of
        rounds is not enough for a word the user says in an unusual way: it has to go on until the
        sentence really comes out right. Returns (weights to keep or None, guard loss, target loss, steps)."""
        per_round = math.ceil(len(lesson) / batch_size)
        planned = rounds * per_round
        run = new_run(planned)
        kept, kept_guard, kept_target = None, None, clean_loss(targets)
        right_rounds, done, began = 0, 0, time.time()
        tag = {"phase": phase} if phase else {}
        emit(stage="train", step=0, total=planned, **tag)
        for r in range(rounds):
            model.train()
            order = random.sample(range(len(lesson)), len(lesson))
            for i in range(0, len(order), batch_size):
                loss = train_on([lesson[j] for j in order[i:i + batch_size]], run)
                done += 1
                emit(stage="train", step=done, total=planned, loss=loss, seconds=time.time() - began, **tag)
            guard_loss, target_loss, right = clean_loss(guard), clean_loss(targets), written_right(targets)
            emit(stage="checked", epoch=r + 1, epochs=rounds, loss=guard_loss, focus=target_loss,
                 right=sum(right), of=len(right), **tag)
            if guard_loss > guard_limit:
                break  # it has started to unlearn what it knew: keep the last good round
            if target_loss < kept_target - 1e-4:
                kept, kept_guard, kept_target = snapshot(), guard_loss, target_loss
            # One more round after they first come out right, so they also do when said a little differently.
            right_rounds = right_rounds + 1 if all(right) else 0
            if right_rounds >= 2:
                break
        return kept, kept_guard, kept_target, done

    # An update adds a few new sentences to a model that was already trained on the user's voice.
    update = bool(focus) and not job.get("final_on_all")
    best_loss, best, step, stale, best_epoch = base_loss, None, 0, 0, 0
    best_focus = base_focus
    started = time.time()
    if update:
        # A little noise on the sentences it already knew is allowed, no more.
        best, kept_loss, best_focus, step = learn(train, focus, holdout, base_loss * 1.1 + 0.02, epochs)
        if best is not None:
            best_loss = kept_loss
    else:
        run = new_run(total_steps)
        emit(stage="train", step=0, total=total_steps, epoch=0, epochs=epochs, base_loss=base_loss)
        for epoch in range(epochs):
            model.train()
            order = random.sample(range(len(train)), len(train))
            for i in range(0, len(order), batch_size):
                loss = train_on([train[j] for j in order[i:i + batch_size]], run)
                step += 1
                emit(stage="train", step=step, total=total_steps, epoch=epoch + 1, epochs=epochs,
                     loss=loss, seconds=time.time() - started)

            current = clean_loss(holdout)
            emit(stage="checked", epoch=epoch + 1, epochs=epochs, loss=current, best=min(best_loss, current))
            if current < best_loss - 1e-4:
                best_loss, best, stale, best_epoch = current, snapshot(), 0, epoch + 1
            else:
                stale += 1
                if stale >= int(job.get("patience", 2)):
                    break  # more training now only memorises the sentences

    improved = best is not None
    if improved:
        with torch.no_grad():
            for p, kept in zip(trainable, best):
                p.copy_(kept)

    # -- the final training, on every recording
    # The run above kept some sentences back to find out how many rounds help before the model starts
    # memorising. Now that this is known, the same training is done again for that many rounds on all
    # the recordings, the held-back ones included, so nothing the user recorded goes unused.
    if improved and not update and job.get("final_on_all"):
        # What the model writes for the held-back sentences before it has trained on them: the fair result.
        emit(stage="fair", texts=transcripts(), loss=best_loss)
        everything = train + holdout
        steps_per_epoch = math.ceil(len(everything) / batch_size)
        with torch.no_grad():
            for a in adapters.values():
                a.a.copy_(torch.randn_like(a.a) / math.sqrt(a.base.in_features))
                a.b.zero_()
        run = new_run(epochs * steps_per_epoch)
        final_steps, done_steps = best_epoch * steps_per_epoch, 0
        final_started = time.time()
        emit(stage="train", phase=2, step=0, total=final_steps, rounds=best_epoch)
        for epoch in range(best_epoch):
            model.train()
            order = random.sample(range(len(everything)), len(everything))
            for i in range(0, len(order), batch_size):
                loss = train_on([everything[j] for j in order[i:i + batch_size]], run)
                done_steps += 1
                emit(stage="train", phase=2, step=done_steps, total=final_steps, loss=loss,
                     seconds=time.time() - final_started)
        step += done_steps
        model.eval()

        # -- extra practice on the user's own sentences and corrections
        # These carry the words the user most wants written right (names, a word said in an unusual
        # way). A couple of rounds among a hundred other sentences is often not enough for them, so the
        # ones that still come out wrong are practised until they come out right.
        own = load(job.get("own", []))
        wrong = [sample for sample, right in zip(own, written_right(own)) if not right] if own else []
        emit(stage="own", wrong=len(wrong), of=len(own))
        if wrong:
            after_final = snapshot()
            count = min(len(everything), max(8, min(40, 2 * len(wrong))))
            reminders = [everything[i * len(everything) // count] for i in range(count)]
            limit = clean_loss(holdout) * 1.1 + 0.02
            kept, _, _, extra = learn(wrong * 3 + reminders, wrong, holdout, limit, int(job.get("own_rounds", 8)), phase=3)
            with torch.no_grad():
                for p, value in zip(trainable, kept if kept is not None else after_final):
                    p.copy_(value)
            step += extra
            model.eval()

    # -- write the adapted model
    if improved:
        emit(stage="saving")
        weights = {}
        with torch.no_grad():
            merged = {name: a.merged().float().cpu().numpy() for name, a in adapters.items()}
            for name, _, _, data in tensors:
                weights[name] = merged[name] if name in merged else data
        write_ggml(job["base"], job["out"], header_bytes, tensors, weights)

    emit(stage="done", improved=improved, base_loss=base_loss, best_loss=best_loss,
         base_focus=base_focus, best_focus=best_focus,
         steps=step, seconds=time.time() - started,
         peak_vram_mb=int(torch.cuda.max_memory_allocated() / 2 ** 20))


if __name__ == "__main__":
    try:
        main(sys.argv[1])
    except Exception as ex:  # reported to the app, which shows it to the user
        message = str(ex) or type(ex).__name__
        if type(ex).__name__ == "OutOfMemoryError":
            message = "The graphics card ran out of memory. Close games and other heavy apps, then try again."
        emit(stage="error", message=message)
        sys.exit(1)
