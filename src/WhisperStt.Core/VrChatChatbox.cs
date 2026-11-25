using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WhisperStt.Core;

// Writes into VRChat's chatbox (the text bubble above the avatar) over OSC, the control protocol VRChat
// listens to on UDP port 9000 of this PC once OSC is enabled in its Action Menu (Options > OSC).
// UDP gives no reply, so a message that was sent is not proof that VRChat showed it.
public sealed class VrChatChatbox : IDisposable
{
    // VRChat cuts chatbox messages at 144 characters.
    public const int MaxLength = 144;

    // Where VRChat listens. Settable so tests can send to a listener of their own instead of a running game.
    public static IPEndPoint Endpoint { get; set; } = new(IPAddress.Loopback, 9000);

    private readonly UdpClient _udp = new(AddressFamily.InterNetwork);

    public static bool IsVrChatRunning => Process.GetProcessesByName("VRChat").Length > 0;

    // Shows the text at once (no keyboard pops up in VRChat) and without the notification sound, which
    // would otherwise play for everyone nearby on every sentence.
    public void Send(string text) =>
        Send(Message("/chatbox/input", Shorten(text), true, false));

    public void Clear() => Send("");

    // The "..." bubble others see while a sentence is on its way.
    public void SetTyping(bool typing) => Send(Message("/chatbox/typing", typing));

    private void Send(byte[] packet) => _udp.Send(packet, packet.Length, Endpoint);

    private static string Shorten(string text)
    {
        var info = new StringInfo(text);
        return info.LengthInTextElements <= MaxLength ? text : info.SubstringByTextElements(0, MaxLength - 1) + "…";
    }

    // An OSC message: the address, a list of argument types, then the arguments. Every part is padded
    // with zero bytes to a multiple of four; true and false are part of the type list and carry no data.
    public static byte[] Message(string address, params object[] arguments)
    {
        var packet = new MemoryStream();
        void WriteString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            packet.Write(bytes);
            packet.Write(new byte[4 - bytes.Length % 4]); // at least one zero ends the string
        }

        WriteString(address);
        WriteString("," + string.Concat(arguments.Select(a => a switch
        {
            string => "s",
            true => "T",
            false => "F",
            _ => throw new ArgumentException($"Unsupported OSC argument: {a}")
        })));
        foreach (var text in arguments.OfType<string>())
            WriteString(text);
        return packet.ToArray();
    }

    public void Dispose() => _udp.Dispose();
}
