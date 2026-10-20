namespace Tostatronic.Cnc.Core.Protocol;

/// <summary>
/// Enlace serial abstracto: el puerto real (CH340) o el simulador de máquina.
/// </summary>
public interface ISerialLink : IDisposable
{
    string Name { get; }
    bool IsOpen { get; }
    event Action<byte[]>? DataReceived;
    void Open();
    void Close();
    void Write(byte[] data);
}
