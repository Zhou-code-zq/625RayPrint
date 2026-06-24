using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace WpfApp1
{
    /// <summary>
    /// App.xaml 的交互逻辑
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                var ex = args.ExceptionObject as Exception;
                MessageBox.Show($"[App全局] 应用程序异常: {ex?.Message}\n{ex?.StackTrace}", "严重错误", MessageBoxButton.OK, MessageBoxImage.Error);
            };
            DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show($"[App全局] UI线程异常: {args.Exception.Message}\n{args.Exception.StackTrace}", "严重错误", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };
            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                MessageBox.Show($"[App全局] Task异常: {args.Exception.Message}", "严重错误", MessageBoxButton.OK, MessageBoxImage.Error);
                args.SetObserved();
            };
        }
    }
}
