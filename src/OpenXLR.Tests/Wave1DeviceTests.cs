using System.Buffers.Binary;
using OpenXLR.Core;
using OpenXLR.Core.Devices;

namespace OpenXLR.Tests;

/// <summary>
/// The Wave:1 is the Wave:3's backend under its own product id and name.
/// These hold that to be the whole difference: the block is decoded the
/// same, the controls offered are the same, and only the id the transport
/// opens, the id the ALSA card is found by and the model name change.
/// Nothing here shows the Wave:1 answers the block at all; an owner does.
/// </summary>
public sealed class Wave1DeviceTests
{
    private sealed class FakeUsb : IUsbTransport
    {
        public byte[] Config { get; set; } = new byte[16];
        public List<byte[]> Writes { get; } = [];
        public (ushort Vid, ushort Pid)? Opened { get; private set; }
        public bool IsOpen { get; private set; }

        public bool Open(ushort vid, ushort pid)
        {
            Opened = (vid, pid);
            IsOpen = true;
            return true;
        }

        public void Close() => IsOpen = false;

        public int ControlTransfer(byte t, byte r, ushort v, ushort i, byte[] data, ushort len, uint timeout)
        {
            Assert.Equal(0x3303, i);
            Assert.True(t is 0xA1 or 0x21, $"request type {t:x2}");   // the class dialect only, as on the Wave:3
            if (t == 0xA1)
            {
                if (v != 0) return -5;   // no device info block on this fake
                Config.AsSpan(0, 16).CopyTo(data);
                return 16;
            }
            data.AsSpan(0, len).CopyTo(Config);
            Writes.Add(data.AsSpan(0, len).ToArray());
            return len;
        }

        public void Dispose() { }
    }

    private static byte[] Block(byte dial = Wave3Device.DialHeadphones)
    {
        var c = new byte[16];
        for (int i = 0; i < c.Length; i++) c[i] = (byte)(0xA0 + i);
        BinaryPrimitives.WriteInt16LittleEndian(c.AsSpan(0), 24 * 256);
        c[4] = 0;
        c[5] = 1;
        BinaryPrimitives.WriteInt16LittleEndian(c.AsSpan(7), (short)(-18 * 256));
        c[9] = 0;
        BinaryPrimitives.WriteInt16LittleEndian(c.AsSpan(10), 35 * 256);
        c[12] = dial;
        return c;
    }

    [Fact]
    public void ItIsTheWave3sBackendUnderItsOwnId()
    {
        var dev = new Wave1Device(new FakeUsb());
        Assert.Equal(new DeviceInfo("Elgato", "Wave:1", 0x0FD9, 0x006E), dev.Info);
        Assert.Equal("Wave_1", dev.Info.NodeNameFragment);
        Assert.Equal(0x006E, Wave1Device.ProductId);
        Assert.Equal(0x0070, Wave3Device.ProductId);
        Assert.Equal(new Wave3Device(new Wave3DeviceTests.FakeUsb()).Capabilities, dev.Capabilities);
    }

    [Fact]
    public void ConnectOpensTheWave1NotTheWave3()
    {
        var usb = new FakeUsb { Config = Block() };
        using var dev = new Wave1Device(usb);
        dev.Connect();
        Assert.Equal(((ushort)0x0FD9, (ushort)0x006E), usb.Opened!.Value);
        Assert.True(dev.Connected);
        dev.Disconnect();
        Assert.False(usb.IsOpen);
    }

    [Fact]
    public void TheBlockIsReadAndWrittenAsOnTheWave3()
    {
        byte[] original = Block();
        var usb = new FakeUsb { Config = [.. original] };
        using var dev = new Wave1Device(usb);
        dev.Connect();
        DeviceState s = dev.ReadState();
        Assert.Equal(24, s.GainDb);
        Assert.False(s.Mute);
        Assert.True(s.ClipGuard);
        Assert.Equal(-18, s.HpVolumeDb);
        Assert.Equal(130, s.Crossfade);        // 35 percent, mirrored as on the Wave:3
        dev.SetMute(true);
        byte[] w = Assert.Single(usb.Writes);
        Assert.Equal(1, w[4]);
        foreach (int i in new[] { 2, 3, 6, 12, 13, 14, 15 }) Assert.Equal(original[i], w[i]);
        // The review gives the dial two targets, headphones and gain; the dump names either.
        Assert.Equal("headphones", dev.DumpBlocks()["dial"]);
    }

    [Fact]
    public void TheCardIsFoundByTheWave1sId()
    {
        string root = Directory.CreateTempSubdirectory("openxlr-wave1-cards").FullName;
        try
        {
            foreach ((int card, string id, string bus) in new[] { (1, "0fd9:0070", "001/004"), (3, "0fd9:006e", "001/007") })
            {
                Directory.CreateDirectory(Path.Combine(root, $"card{card}"));
                File.WriteAllText(Path.Combine(root, $"card{card}", "usbid"), id + "\n");
                File.WriteAllText(Path.Combine(root, $"card{card}", "usbbus"), bus + "\n");
            }
            Assert.Equal(3, AlsaCards.Find(root, new UsbLocation(1, 7, "1-3", null), "0fd9:006e", "Wave:1"));
            Assert.Equal(3, AlsaCards.Find(root, null, "0fd9:006e", "Wave:1"));
        }
        finally { Directory.Delete(root, true); }
    }
}
