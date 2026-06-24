using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;

namespace WpfApp1
{
    public partial class MainWindow : Window
    {
        // 页面实例引用
        private VisionInspectionPage _visionPage;
        private ParameterConfigPage _configPage;

        public MainWindow()
        {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 页面加载时初始化子页面
            InitializePages();
        }

        private void InitializePages()
        {
            try
            {
                // 创建参数配置页面实例并订阅保存事件
                _configPage = new ParameterConfigPage();
                _configPage.ConfigSaved += OnConfigSaved;

                // 导航到参数配置页面
                ConfigFrame.Navigate(_configPage);

                // 动态创建质检与监控页面（避免 XAML 声明式创建时构造函数异常被静默忽略）
                try
                {
                    uint sdkDeviceType = MvCamCtrl.NET.MyCamera.MV_GIGE_DEVICE | MvCamCtrl.NET.MyCamera.MV_USB_DEVICE;
                    _visionPage = new VisionInspectionPage("", sdkDeviceType);
                    VisionTabItem.Content = _visionPage;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"创建质检与监控页面失败: {ex}", "严重错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"初始化页面失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnConfigSaved(int deviceType, string serialNo, string ipAddress)
        {
            // 将配置传递给视觉检测页面
            // deviceType 来自 ComboBox 索引，我们始终枚举所有类型，用序列号匹配
            if (_visionPage != null)
            {
                uint sdkDeviceType = MvCamCtrl.NET.MyCamera.MV_GIGE_DEVICE | MvCamCtrl.NET.MyCamera.MV_USB_DEVICE;
                VisionInspectionPage.SetCameraConfig(serialNo, sdkDeviceType);
            }
        }

        // 获取参数配置页面实例
        public static ParameterConfigPage GetConfigPage()
        {
            var mainWindow = Application.Current.MainWindow as MainWindow;
            return mainWindow?._configPage;
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
