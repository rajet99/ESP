using System;
using System.IO.Ports;

namespace GroundStation.Core;

public sealed class SerialTransport : IDisposable
{
    private SerialPort? _serialPort;
    private readonly object _lock = new();

    public bool IsOpen => _serialPort?.IsOpen ?? false;
    public string? CurrentPortName => _serialPort?.PortName;

    public static string[] GetAvailablePorts()
    {
        try
        {
            return SerialPort.GetPortNames();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public bool Open(string portName, int baudRate = 921600)
    {
        lock (_lock)
        {
            Close();

            try
            {
                _serialPort = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
                {
                    WriteTimeout = 50,
                    ReadTimeout = 50,
                    // Keep DTR/RTS deasserted: ESP32 auto-reset circuits map these to
                    // GPIO0/EN, and asserting them holds the chip in reset/bootloader
                    // and conflicts with esptool flashing.
                    DtrEnable = false,
                    RtsEnable = false
                };

                _serialPort.Open();
                return true;
            }
            catch (Exception)
            {
                _serialPort?.Dispose();
                _serialPort = null;
                return false;
            }
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            if (_serialPort != null)
            {
                try
                {
                    if (_serialPort.IsOpen)
                    {
                        _serialPort.Close();
                    }
                }
                catch { }
                finally
                {
                    _serialPort.Dispose();
                    _serialPort = null;
                }
            }
        }
    }

    public bool WriteFrame(byte[] buffer)
    {
        if (_serialPort == null || !_serialPort.IsOpen) return false;

        try
        {
            _serialPort.Write(buffer, 0, buffer.Length);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        Close();
    }
}
