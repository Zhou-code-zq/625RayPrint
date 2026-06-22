using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace WpfApp1
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        // 无边框窗口拖拽
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        // 退出系统（适配TabItem的点击事件）
        private void BtnExit_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 弹出确认框
            MessageBoxResult result = MessageBox.Show(
                "确定要退出控制系统吗？",
                "退出确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                // 关闭当前窗口，退出程序
                this.Close();
                // 如需强制退出整个进程，可使用：
                // Environment.Exit(0);
            }
        }
    }
}