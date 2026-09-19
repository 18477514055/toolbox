namespace Poc.Contracts;

/// <summary>插件契约：所有工具插件都实现它。</summary>
public interface IPocTool
{
    string Id { get; }
    string Name { get; }

    /// <summary>由宿主注入的能力（设置、日志…）。</summary>
    void Attach(IPocHost host);

    /// <summary>打开这个工具的界面（真实工具都要开窗口）。</summary>
    void Open();
}

/// <summary>宿主提供给插件的能力。插件只依赖这个接口，不依赖宿主实现。</summary>
public interface IPocHost
{
    string GetSetting(string key);
    void Log(string message);
}
