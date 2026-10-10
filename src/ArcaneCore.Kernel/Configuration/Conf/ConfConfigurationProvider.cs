using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using Microsoft.Extensions.Primitives;

namespace ArcaneCore.Kernel.Configuration.Conf;

/// <summary>
/// Reads a .conf file into configuration. Deliberately not a <c>FileConfigurationProvider</c>: that class clears its data
/// when a reload fails, which would empty the live configuration on a typo. Here the first load throws
/// (<see cref="ConfParseException"/>, <see cref="FileNotFoundException"/>), and every later failure (a malformed edit, a
/// deleted file, a read error) keeps the last good values and is reported through <see cref="ConfConfigurationSource.ReloadError"/>.
/// Thread affinity: <see cref="Load"/> runs on the builder's thread, reloads run on a thread-pool thread; the data dictionary is
/// replaced wholesale, never mutated.
/// </summary>
public sealed class ConfConfigurationProvider : ConfigurationProvider, IDisposable
{
    private readonly ConfConfigurationSource _source;
    private readonly object _gate = new();
    private PhysicalFileProvider? _watcher;
    private IDisposable? _registration;
    private ConfDocument? _document;
    private bool _loaded;

    public ConfConfigurationProvider(ConfConfigurationSource source)
    {
        _source = source;
    }

    /// <summary>The file this provider reads.</summary>
    public string Path => _source.Path;

    /// <summary>The last good parse, with line numbers, or null when the file was absent (optional) or not read yet.</summary>
    public ConfDocument? Document => Volatile.Read(ref _document);

    public override void Load()
    {
        lock (_gate)
        {
            Apply(Read(), reload: false);
            _loaded = true;
            if (_source.ReloadOnChange && _watcher is null)
            {
                StartWatching();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _registration?.Dispose();
            _registration = null;
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private (ConfDocument? Document, Dictionary<string, string?> Data) Read()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_source.Path))
        {
            if (_source.Optional)
            {
                return (null, data);
            }

            throw new FileNotFoundException($"the configuration file {_source.Path} was not found", _source.Path);
        }

        ConfDocument document = ConfDocument.Load(_source.Path);
        foreach (ConfEntry entry in document.Entries)
        {
            data[entry.Path] = entry.Value;
        }

        return (document, data);
    }

    private void Apply((ConfDocument? Document, Dictionary<string, string?> Data) read, bool reload)
    {
        Data = read.Data;
        Volatile.Write(ref _document, read.Document);
        if (reload)
        {
            OnReload();
        }
    }

    private void StartWatching()
    {
        string? directory = System.IO.Path.GetDirectoryName(_source.Path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        string fileName = System.IO.Path.GetFileName(_source.Path);
        _watcher = new PhysicalFileProvider(directory, ExclusionFilters.None);
        _registration = ChangeToken.OnChange(() => _watcher.Watch(fileName), Reload);
    }

    private void Reload()
    {
        Thread.Sleep(_source.ReloadDelay);
        lock (_gate)
        {
            if (!_loaded || _watcher is null)
            {
                return;
            }

            try
            {
                if (!File.Exists(_source.Path))
                {
                    _source.ReloadError?.Invoke($"{System.IO.Path.GetFileName(_source.Path)}: the file is gone; keeping the last good values.");
                    return;
                }

                Apply(Read(), reload: true);
            }
            catch (ConfParseException ex)
            {
                _source.ReloadError?.Invoke(ex.Message + $"{Environment.NewLine}{System.IO.Path.GetFileName(_source.Path)}: not applied; keeping the last good values.");
            }
            catch (IOException ex)
            {
                _source.ReloadError?.Invoke($"{System.IO.Path.GetFileName(_source.Path)}: cannot be read ({ex.Message}); keeping the last good values.");
            }
            catch (UnauthorizedAccessException ex)
            {
                _source.ReloadError?.Invoke($"{System.IO.Path.GetFileName(_source.Path)}: cannot be read ({ex.Message}); keeping the last good values.");
            }
        }
    }
}
