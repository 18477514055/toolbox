using System.ComponentModel;
using System.Runtime.CompilerServices;
using Path = System.IO.Path;

namespace Toolbox.Tools.Convert;

/// <summary>队列里的一项转换任务。</summary>
internal sealed class ConvertJob : INotifyPropertyChanged
{
    private string _selectedTarget = "";
    private string _status = "等待";
    private string? _outputPath;
    private bool _isRunning;

    public ConvertJob(string sourcePath)
    {
        SourcePath = sourcePath;
        Targets = Formats.TargetsFor(sourcePath);
        _selectedTarget = Targets.FirstOrDefault() ?? "";
    }

    public string SourcePath { get; }

    public string FileName => Path.GetFileName(SourcePath);

    public string Folder => Path.GetDirectoryName(SourcePath) ?? "";

    public string Kind => Formats.Describe(SourcePath);

    public List<string> Targets { get; }

    public string SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (_selectedTarget == value)
            {
                return;
            }

            _selectedTarget = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TargetHint));
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            OnPropertyChanged();
        }
    }

    public string? OutputPath
    {
        get => _outputPath;
        set
        {
            _outputPath = value;
            OnPropertyChanged();
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value)
            {
                return;
            }

            _isRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RunningVisibility));
        }
    }

    /// <summary>
    /// 直接给出 Visibility，而不是写一个 Bool→Visibility 转换器。
    /// 这个项目里这类"显示/隐藏"的判断只有几处，写转换器反而是更多代码和更多间接层。
    /// </summary>
    public System.Windows.Visibility RunningVisibility
        => _isRunning ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    /// <summary>给用户看的"这一步靠什么转"。</summary>
    public string TargetHint => Formats.NeedsExternalBackend(SourcePath, SelectedTarget)
        ? (Formats.IsPdf(SourcePath) ? "需要 Office" : "需要 LibreOffice")
        : "内置";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
