using System;
using System.Threading;
using System.Windows;

// 自包含单文件发布下，OpenFolderDialog 到底能不能弹出来？
var code = 0;
var t = new Thread(() =>
{
    try
    {
        _ = new Application();
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "测试" };
        Console.WriteLine("构造成功，准备 ShowDialog…");
        // 用定时器自动关掉，避免无人操作卡住
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Console.WriteLine("（3 秒到，自动关闭对话框）");
            Application.Current.Shutdown();
        };
        timer.Start();
        var r = dlg.ShowDialog();
        Console.WriteLine($"ShowDialog 返回：{r}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ {ex.GetType().Name}: {ex.Message}");
        code = 1;
    }
});
t.SetApartmentState(ApartmentState.STA);
t.Start();
t.Join();
return code;
