using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WpfApp1
{
    public partial class ParameterConfigPage : UserControl
    {
        private string _configFilePath = "camera_config.ini";
        
        // 事件：配置保存后触发，用于通知其他页面更新
        // 参数：deviceType(设备类型0=MV系列,1=CA系列,2=CH系列), serialNo, ipAddress
        public event Action<int, string, string> ConfigSaved;

        public ParameterConfigPage()
        {
            InitializeComponent();
            LoadConfig();
        }

        private void ConnectionType_Changed(object sender, RoutedEventArgs e)
        {
            if (RbtSerialNo != null && IpPanel != null)
            {
                if (RbtSerialNo.IsChecked == true)
                {
                    IpPanel.Visibility = Visibility.Collapsed;
                    LblSerialNo.Visibility = Visibility.Visible;
                    TxtSerialNo.Visibility = Visibility.Visible;
                }
                else
                {
                    IpPanel.Visibility = Visibility.Visible;
                    LblSerialNo.Visibility = Visibility.Collapsed;
                    TxtSerialNo.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void BtnTestConnection_Click(object sender, RoutedEventArgs e)
        {
            string serialNo = TxtSerialNo.Text.Trim();
            string ipAddress = TxtIpAddress.Text.Trim();
            string connectionInfo = RbtSerialNo.IsChecked == true ? serialNo : ipAddress;

            if (string.IsNullOrWhiteSpace(connectionInfo))
            {
                ShowTestResult(false, "请输入设备序列号或IP地址");
                return;
            }

            BtnTestConnection.IsEnabled = false;
            BtnTestConnection.Content = "测试中...";

            // 模拟测试
            System.Threading.Thread.Sleep(1000);

            BtnTestConnection.IsEnabled = true;
            BtnTestConnection.Content = "测试连接";

            if (connectionInfo.Length >= 6)
            {
                ShowTestResult(true, $"设备连接测试成功");
            }
            else
            {
                ShowTestResult(false, $"设备 [{connectionInfo}] 未找到，请检查配置");
            }
        }

        private void ShowTestResult(bool success, string message)
        {
            TestResultPanel.Visibility = Visibility.Visible;
            TestResultText.Text = message;

            if (success)
            {
                TestResultIcon.Text = "✓";
                TestResultIcon.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
                TestResultText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
                if (TestResultPanel.Children.Count > 0 && TestResultPanel.Children[0] is Border border)
                {
                    border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F0FDF4"));
                }
            }
            else
            {
                TestResultIcon.Text = "✗";
                TestResultIcon.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
                TestResultText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
                if (TestResultPanel.Children.Count > 0 && TestResultPanel.Children[0] is Border border)
                {
                    border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FEF2F2"));
                }
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 验证必填项
                if (RbtSerialNo.IsChecked == true && string.IsNullOrWhiteSpace(TxtSerialNo.Text))
                {
                    MessageBox.Show("请输入设备序列号", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (RbtIp.IsChecked == true && string.IsNullOrWhiteSpace(TxtIpAddress.Text))
                {
                    MessageBox.Show("请输入IP地址", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 保存配置到文件
                SaveConfig();

                // 获取设备类型
                int deviceType = DeviceTypeCombo.SelectedIndex;

                // 触发保存事件
                string serialNo = TxtSerialNo.Text.Trim();
                string ipAddress = TxtIpAddress.Text.Trim();
                ConfigSaved?.Invoke(deviceType, serialNo, ipAddress);

                MessageBox.Show("配置保存成功！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存配置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("确定要重置所有配置吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                TxtSerialNo.Text = "";
                TxtIpAddress.Text = "";
                TxtUsername.Text = "";
                TxtPassword.Password = "";
                TxtTimeout.Text = "3000";
                TxtRetryCount.Text = "3";
                RbtSerialNo.IsChecked = true;
                ChkAutoConnect.IsChecked = false;
                ChkSaveLog.IsChecked = true;
                DeviceTypeCombo.SelectedIndex = 0;
                TestResultPanel.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// 加载配置文件
        /// </summary>
        public void LoadConfig()
        {
            try
            {
                if (File.Exists(_configFilePath))
                {
                    string[] lines = File.ReadAllLines(_configFilePath);
                    foreach (string line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                            continue;

                        string[] parts = line.Split('=');
                        if (parts.Length != 2)
                            continue;

                        string key = parts[0].Trim();
                        string value = parts[1].Trim();

                        switch (key)
                        {
                            case "device_type":
                                if (int.TryParse(value, out int dt))
                                    DeviceTypeCombo.SelectedIndex = dt;
                                break;
                            case "serial_no":
                                TxtSerialNo.Text = value;
                                break;
                            case "ip_address":
                                TxtIpAddress.Text = value;
                                if (!string.IsNullOrEmpty(value))
                                {
                                    RbtIp.IsChecked = true;
                                }
                                break;
                            case "username":
                                TxtUsername.Text = value;
                                break;
                            case "timeout":
                                TxtTimeout.Text = value;
                                break;
                            case "retry_count":
                                TxtRetryCount.Text = value;
                                break;
                            case "auto_connect":
                                ChkAutoConnect.IsChecked = value == "1" || value.ToLower() == "true";
                                break;
                            case "save_log":
                                ChkSaveLog.IsChecked = value != "0" && value.ToLower() != "false";
                                break;
                        }
                    }
                }
            }
            catch
            {
                // 使用默认配置
            }
        }

        /// <summary>
        /// 保存配置到文件
        /// </summary>
        private void SaveConfig()
        {
            try
            {
                using (StreamWriter writer = new StreamWriter(_configFilePath))
                {
                    writer.WriteLine("# 海康相机配置文件");
                    writer.WriteLine($"device_type={DeviceTypeCombo.SelectedIndex}");
                    writer.WriteLine($"serial_no={TxtSerialNo.Text}");
                    writer.WriteLine($"ip_address={TxtIpAddress.Text}");
                    writer.WriteLine($"username={TxtUsername.Text}");
                    writer.WriteLine($"timeout={TxtTimeout.Text}");
                    writer.WriteLine($"retry_count={TxtRetryCount.Text}");
                    writer.WriteLine($"auto_connect={(ChkAutoConnect.IsChecked == true ? "1" : "0")}");
                    writer.WriteLine($"save_log={(ChkSaveLog.IsChecked == true ? "1" : "0")}");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存配置文件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 获取当前配置
        /// </summary>
        public (int DeviceType, string SerialNo, string IpAddress) GetCurrentConfig()
        {
            return (DeviceTypeCombo.SelectedIndex, TxtSerialNo.Text.Trim(), TxtIpAddress.Text.Trim());
        }
    }
}
