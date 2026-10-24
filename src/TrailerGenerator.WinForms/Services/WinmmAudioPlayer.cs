using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TrailerGenerator.WinForms.Services;

/// <summary>
/// Low-latency uncompressed PCM streaming audio player using native Windows Multimedia API (winmm.dll / waveOut).
/// </summary>
public sealed class WinmmAudioPlayer : IDisposable
{
    private const uint WHDR_DONE = 0x00000001;
    private const uint WHDR_PREPARED = 0x00000002;
    private const uint WAVE_MAPPER = unchecked((uint)-1);
    private const int BufferCount = 8;
    private const int BufferSize = 17640; // 0.1s of 44.1kHz 16-bit stereo PCM (44100 * 2 * 2 * 0.1)

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll", EntryPoint = "waveOutOpen", SetLastError = true)]
    private static extern int WaveOutOpen(out IntPtr hWaveOut, uint uDeviceID, ref WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, uint dwFlags);

    [DllImport("winmm.dll", EntryPoint = "waveOutPrepareHeader", SetLastError = true)]
    private static extern int WaveOutPrepareHeader(IntPtr hWaveOut, IntPtr lpWaveHdr, uint uSize);

    [DllImport("winmm.dll", EntryPoint = "waveOutWrite", SetLastError = true)]
    private static extern int WaveOutWrite(IntPtr hWaveOut, IntPtr lpWaveHdr, uint uSize);

    [DllImport("winmm.dll", EntryPoint = "waveOutUnprepareHeader", SetLastError = true)]
    private static extern int WaveOutUnprepareHeader(IntPtr hWaveOut, IntPtr lpWaveHdr, uint uSize);

    [DllImport("winmm.dll", EntryPoint = "waveOutReset", SetLastError = true)]
    private static extern int WaveOutReset(IntPtr hWaveOut);

    [DllImport("winmm.dll", EntryPoint = "waveOutClose", SetLastError = true)]
    private static extern int WaveOutClose(IntPtr hWaveOut);

    private readonly object _lock = new();
    private readonly int _flagsOffset = Marshal.OffsetOf<WAVEHDR>(nameof(WAVEHDR.dwFlags)).ToInt32();
    private IntPtr _hWaveOut = IntPtr.Zero;
    private AudioBufferSlot[]? _slots;
    private bool _isDisposed;

    private float _volume = 0.5f;

    /// <summary>
    /// Gets or sets whether playback is muted.
    /// When muted, audio buffers are silenced before playback to maintain synchronization.
    /// </summary>
    public bool IsMuted { get; set; }

    /// <summary>
    /// Optional dynamic delegate to query mute status per buffer frame.
    /// </summary>
    public Func<bool>? IsMutedProvider { get; set; }

    /// <summary>
    /// Gets or sets the playback volume (0.0f = silent, 1.0f = max).
    /// </summary>
    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0.0f, 1.0f);
    }

    /// <summary>
    /// Optional dynamic delegate to query volume level per buffer frame.
    /// </summary>
    public Func<float>? VolumeProvider { get; set; }

    public WinmmAudioPlayer(Func<bool>? isMutedProvider = null, Func<float>? volumeProvider = null)
    {
        IsMutedProvider = isMutedProvider;
        VolumeProvider = volumeProvider;
    }

    /// <summary>
    /// Plays raw 44.1kHz 16-bit stereo PCM audio streamed from the provided stream.
    /// </summary>
    public async Task PlayPcmStreamAsync(Stream pcmStream, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        lock (_lock)
        {
            if (_slots == null)
            {
                _slots = new AudioBufferSlot[BufferCount];
                for (int i = 0; i < BufferCount; i++)
                {
                    _slots[i] = new AudioBufferSlot(BufferSize);
                }
            }

            if (_hWaveOut == IntPtr.Zero)
            {
                var format = new WAVEFORMATEX
                {
                    wFormatTag = 1, // WAVE_FORMAT_PCM
                    nChannels = 2,
                    nSamplesPerSec = 44100,
                    wBitsPerSample = 16,
                    nBlockAlign = 4,
                    nAvgBytesPerSec = 176400,
                    cbSize = 0
                };

                int openResult = WaveOutOpen(out _hWaveOut, WAVE_MAPPER, ref format, IntPtr.Zero, IntPtr.Zero, 0);
                if (openResult != 0 || _hWaveOut == IntPtr.Zero)
                {
                    Debug.WriteLine($"waveOutOpen failed with error code: {openResult}");
                    _hWaveOut = IntPtr.Zero;
                    return;
                }
            }
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested && !_isDisposed)
            {
                AudioBufferSlot? freeSlot = null;

                lock (_lock)
                {
                    if (_slots == null || _hWaveOut == IntPtr.Zero)
                    {
                        break;
                    }

                    for (int i = 0; i < BufferCount; i++)
                    {
                        var slot = _slots[i];
                        if (!slot.InFlight)
                        {
                            freeSlot = slot;
                            break;
                        }

                        uint flags = slot.GetFlags(_flagsOffset);
                        if ((flags & WHDR_DONE) != 0)
                        {
                            if (slot.IsPrepared && _hWaveOut != IntPtr.Zero)
                            {
                                WaveOutUnprepareHeader(_hWaveOut, slot.HeaderPtr, (uint)Marshal.SizeOf<WAVEHDR>());
                                slot.IsPrepared = false;
                            }
                            slot.InFlight = false;
                            freeSlot = slot;
                            break;
                        }
                    }
                }

                if (freeSlot == null)
                {
                    await Task.Delay(15, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // Read exactly up to BufferSize bytes or EOF
                int totalRead = 0;
                while (totalRead < BufferSize)
                {
                    int bytesRead = await pcmStream.ReadAsync(
                        freeSlot.Buffer.AsMemory(totalRead, BufferSize - totalRead),
                        cancellationToken).ConfigureAwait(false);

                    if (bytesRead <= 0)
                    {
                        break;
                    }
                    totalRead += bytesRead;
                }

                if (totalRead == 0)
                {
                    // Stream ended (EOF)
                    break;
                }

                bool muted = IsMuted || (IsMutedProvider != null && IsMutedProvider());
                float vol = VolumeProvider != null ? VolumeProvider() : Volume;
                vol = Math.Clamp(vol, 0.0f, 1.0f);

                if (muted || vol <= 0.001f)
                {
                    Array.Clear(freeSlot.Buffer, 0, totalRead);
                }
                else if (vol < 0.999f)
                {
                    Span<short> samples = MemoryMarshal.Cast<byte, short>(freeSlot.Buffer.AsSpan(0, totalRead));
                    for (int i = 0; i < samples.Length; i++)
                    {
                        samples[i] = (short)Math.Clamp((int)Math.Round(samples[i] * vol), short.MinValue, short.MaxValue);
                    }
                }

                lock (_lock)
                {
                    if (_isDisposed || _hWaveOut == IntPtr.Zero)
                    {
                        break;
                    }

                    freeSlot.SetBufferLength((uint)totalRead);
                    int prepRes = WaveOutPrepareHeader(_hWaveOut, freeSlot.HeaderPtr, (uint)Marshal.SizeOf<WAVEHDR>());
                    if (prepRes == 0)
                    {
                        freeSlot.IsPrepared = true;
                        int writeRes = WaveOutWrite(_hWaveOut, freeSlot.HeaderPtr, (uint)Marshal.SizeOf<WAVEHDR>());
                        if (writeRes == 0)
                        {
                            freeSlot.InFlight = true;
                        }
                        else
                        {
                            WaveOutUnprepareHeader(_hWaveOut, freeSlot.HeaderPtr, (uint)Marshal.SizeOf<WAVEHDR>());
                            freeSlot.IsPrepared = false;
                            freeSlot.InFlight = false;
                        }
                    }
                }
            }

            // Wait for remaining in-flight audio buffers to finish playing
            while (!cancellationToken.IsCancellationRequested && !_isDisposed)
            {
                bool anyInFlight = false;
                lock (_lock)
                {
                    if (_slots != null && _hWaveOut != IntPtr.Zero)
                    {
                        for (int i = 0; i < BufferCount; i++)
                        {
                            var slot = _slots[i];
                            if (slot.InFlight)
                            {
                                uint flags = slot.GetFlags(_flagsOffset);
                                if ((flags & WHDR_DONE) != 0)
                                {
                                    if (slot.IsPrepared && _hWaveOut != IntPtr.Zero)
                                    {
                                        WaveOutUnprepareHeader(_hWaveOut, slot.HeaderPtr, (uint)Marshal.SizeOf<WAVEHDR>());
                                        slot.IsPrepared = false;
                                    }
                                    slot.InFlight = false;
                                }
                                else
                                {
                                    anyInFlight = true;
                                }
                            }
                        }
                    }
                }

                if (!anyInFlight)
                {
                    break;
                }

                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            CleanupWaveOut();
        }
    }

    private void CleanupWaveOut()
    {
        lock (_lock)
        {
            if (_hWaveOut != IntPtr.Zero)
            {
                try
                {
                    WaveOutReset(_hWaveOut);
                }
                catch { }

                if (_slots != null)
                {
                    for (int i = 0; i < _slots.Length; i++)
                    {
                        var slot = _slots[i];
                        if (slot.IsPrepared)
                        {
                            try
                            {
                                WaveOutUnprepareHeader(_hWaveOut, slot.HeaderPtr, (uint)Marshal.SizeOf<WAVEHDR>());
                            }
                            catch { }
                            slot.IsPrepared = false;
                        }
                        slot.InFlight = false;
                    }
                }

                try
                {
                    WaveOutClose(_hWaveOut);
                }
                catch { }

                _hWaveOut = IntPtr.Zero;
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        CleanupWaveOut();

        lock (_lock)
        {
            if (_slots != null)
            {
                for (int i = 0; i < _slots.Length; i++)
                {
                    _slots[i]?.Dispose();
                }
                _slots = null;
            }
        }
    }

    private sealed class AudioBufferSlot : IDisposable
    {
        public byte[] Buffer { get; }
        public GCHandle DataPin { get; private set; }
        public IntPtr HeaderPtr { get; private set; }
        public bool IsPrepared { get; set; }
        public bool InFlight { get; set; }

        public AudioBufferSlot(int size)
        {
            Buffer = new byte[size];
            DataPin = GCHandle.Alloc(Buffer, GCHandleType.Pinned);
            HeaderPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());

            var hdr = new WAVEHDR
            {
                lpData = DataPin.AddrOfPinnedObject(),
                dwBufferLength = (uint)size,
                dwBytesRecorded = 0,
                dwUser = IntPtr.Zero,
                dwFlags = 0,
                dwLoops = 0,
                lpNext = IntPtr.Zero,
                reserved = IntPtr.Zero
            };
            Marshal.StructureToPtr(hdr, HeaderPtr, false);
        }

        public uint GetFlags(int flagsOffset)
        {
            return (uint)Marshal.ReadInt32(HeaderPtr, flagsOffset);
        }

        public void SetBufferLength(uint length)
        {
            var hdr = Marshal.PtrToStructure<WAVEHDR>(HeaderPtr);
            hdr.dwBufferLength = length;
            hdr.dwFlags = 0;
            Marshal.StructureToPtr(hdr, HeaderPtr, false);
        }

        public void Dispose()
        {
            if (DataPin.IsAllocated)
            {
                DataPin.Free();
            }
            if (HeaderPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(HeaderPtr);
                HeaderPtr = IntPtr.Zero;
            }
        }
    }
}
