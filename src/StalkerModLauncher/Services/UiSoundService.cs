using System.Reflection;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace StalkerModLauncher.Services;

public enum UiSoundEffect
{
    ButtonPress,
    ProfileActionsOpened,
    ProfileActionsClosed
}

public sealed class UiSoundService : IDisposable
{
    private const float Volume = 0.45f;
    private const string ResourcePrefix = "StalkerModLauncher.Resources.Sounds.";
    private readonly object _sync = new();
    private readonly Dictionary<UiSoundEffect, string> _soundFiles = new();
    private readonly Dictionary<UiSoundEffect, CachedSound> _sounds = new();
    private readonly string _cacheDirectory;
    private MixingSampleProvider? _mixer;
    private WaveOutEvent? _output;
    private bool _isDisposed;
    private bool _isInitialized;

    public UiSoundService()
    {
        _cacheDirectory = Path.Combine(
            AppPaths.Current.CacheDirectory,
            "RuntimeSounds");
    }

    /// <summary>
    /// Opens the output device and decodes the short interface effects once.
    /// Keeping this pipeline alive prevents a first click from being lost while
    /// Windows initializes a newly created audio device.
    /// </summary>
    public void Initialize()
    {
        if (_isDisposed || _isInitialized)
        {
            return;
        }

        try
        {
            lock (_sync)
            {
                if (_isDisposed || _isInitialized)
                {
                    return;
                }

                var buttonPress = LoadSound(UiSoundEffect.ButtonPress);
                var opened = LoadSound(UiSoundEffect.ProfileActionsOpened);
                var closed = LoadSound(UiSoundEffect.ProfileActionsClosed);

                if (!buttonPress.WaveFormat.Equals(opened.WaveFormat) ||
                    !buttonPress.WaveFormat.Equals(closed.WaveFormat))
                {
                    throw new InvalidOperationException("Interface sound files must use the same audio format.");
                }

                _mixer = new MixingSampleProvider(buttonPress.WaveFormat)
                {
                    ReadFully = true
                };
                _output = new WaveOutEvent
                {
                    // Three 80 ms buffers are stable even on busy or older audio
                    // drivers. The output is already running, so this does not
                    // reintroduce the missed first-click issue.
                    DesiredLatency = 240,
                    NumberOfBuffers = 3,
                    Volume = Volume
                };
                _output.Init(_mixer);
                _output.Play();
                _isInitialized = true;
            }
        }
        catch
        {
            // Interface sounds are optional and must never interrupt the launcher.
            ResetAfterInitializationFailure();
        }
    }

    public void Play(UiSoundEffect effect)
    {
        Initialize();

        lock (_sync)
        {
            if (_isDisposed || !_isInitialized || _mixer is null ||
                !_sounds.TryGetValue(effect, out var sound))
            {
                return;
            }

            try
            {
                // A new provider starts at the beginning, so several rapid clicks
                // can overlap without reopening the audio device.
                _mixer.AddMixerInput(new CachedSoundSampleProvider(sound));
            }
            catch
            {
                // Keep the UI responsive if the current output device disappears.
            }
        }
    }

    public void Dispose()
    {
        WaveOutEvent? output;
        lock (_sync)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _isInitialized = false;
            _mixer = null;
            _sounds.Clear();
            output = _output;
            _output = null;
        }

        try
        {
            output?.Stop();
            output?.Dispose();
        }
        catch
        {
            // The output device may already have been disconnected by Windows.
        }
    }

    private CachedSound LoadSound(UiSoundEffect effect)
    {
        var soundPath = GetSoundFile(effect);
        using var reader = new VorbisWaveReader(soundPath);
        var source = reader.ToSampleProvider();
        var samples = new List<float>();
        var buffer = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];

        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var index = 0; index < read; index++)
            {
                samples.Add(buffer[index]);
            }
        }

        var sound = new CachedSound(source.WaveFormat, samples.ToArray());
        _sounds[effect] = sound;
        return sound;
    }

    private string GetSoundFile(UiSoundEffect effect)
    {
        if (_soundFiles.TryGetValue(effect, out var path))
        {
            return path;
        }

        var fileName = effect switch
        {
            UiSoundEffect.ButtonPress => "pda_btn_press.ogg",
            UiSoundEffect.ProfileActionsOpened => "pda_guide.ogg",
            UiSoundEffect.ProfileActionsClosed => "pda_guide_2.ogg",
            _ => throw new ArgumentOutOfRangeException(nameof(effect), effect, null)
        };

        Directory.CreateDirectory(_cacheDirectory);
        path = Path.Combine(_cacheDirectory, fileName);

        using var source = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(ResourcePrefix + fileName)
            ?? throw new InvalidOperationException($"Sound resource '{fileName}' was not found.");
        using var destination = File.Create(path);
        source.CopyTo(destination);

        _soundFiles[effect] = path;
        return path;
    }

    private void ResetAfterInitializationFailure()
    {
        WaveOutEvent? output;
        lock (_sync)
        {
            _isInitialized = false;
            _sounds.Clear();
            _mixer = null;
            output = _output;
            _output = null;
        }

        try
        {
            output?.Dispose();
        }
        catch
        {
            // No action is needed for an output that did not finish initialization.
        }
    }

    private sealed class CachedSound(WaveFormat waveFormat, float[] samples)
    {
        public WaveFormat WaveFormat { get; } = waveFormat;
        public float[] Samples { get; } = samples;
    }

    private sealed class CachedSoundSampleProvider(CachedSound sound) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat => sound.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var available = Math.Min(count, sound.Samples.Length - _position);
            if (available <= 0)
            {
                return 0;
            }

            Array.Copy(sound.Samples, _position, buffer, offset, available);
            _position += available;
            return available;
        }
    }
}
