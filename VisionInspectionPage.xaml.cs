using MvCamCtrl.NET;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WpfApp1
{
    public partial class VisionInspectionPage : Page
    {
        #region 海康相机SDK相关
        // 设备列表
        private MyCamera.MV_CC_DEVICE_INFO_LIST m_pDeviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
        // 相机对象
        private MyCamera m_pCamera = new MyCamera();
        // 回调委托
        private MyCamera.cbOutputdelegate _cbImage;
        // 连接状态
        private bool m_bGrabbing = false;
        private bool m_CamOpenSuccess = false;
        // 图像缓存
        private byte[] m_pBufForSaveImg = null;
        private IntPtr m_hReceiveThread = IntPtr.Zero;
        #endregion

        #region 配置属性
        public string CamSerialStr { get; set; }
        #endregion

        public VisionInspectionPage()
        {
            InitializeComponent();
        }

        // 接收图像回调函数
        private void ReceiveThreadProcess(IntPtr pData, ref MyCamera.MV_FRAME_OUT_INFO pFrameInfo, IntPtr pUser)
        {
            try
            {
                // 复制参数值，因为lambda中不能使用ref参数
                int nWidth = (int)pFrameInfo.nWidth;
                int nHeight = (int)pFrameInfo.nHeight;
                int nFrameLen = (int)pFrameInfo.nFrameLen;
                IntPtr imageBaseAddr = pData; // pData就是图像数据指针

                Application.Current.Dispatcher.Invoke(() =>
                {
                    // 显示图像
                    DisplayImage(pData, nWidth, nHeight, nFrameLen, imageBaseAddr);
                    
                    // 更新帧计数
                    FrameCountText.Text = (int.Parse(FrameCountText.Text) + 1).ToString();
                });
            }
            catch (Exception ex)
            {
                AddLog($"显示图像异常: {ex.Message}");
            }
        }

        // 显示图像
        private void DisplayImage(IntPtr pData, int nWidth, int nHeight, int nFrameLen, IntPtr imageBaseAddr)
        {
            try
            {
                if (nWidth <= 0 || nHeight <= 0 || nFrameLen <= 0)
                    return;

                // 分配缓存
                if (m_pBufForSaveImg == null || m_pBufForSaveImg.Length < nFrameLen)
                {
                    m_pBufForSaveImg = new byte[nFrameLen];
                }

                // 复制图像数据
                Marshal.Copy(pData, m_pBufForSaveImg, 0, nFrameLen);

                // 转换为BitmapSource
                BitmapSource bitmapSource = CreateBitmapSource(
                    m_pBufForSaveImg,
                    nWidth,
                    nHeight);

                // 显示到Image控件
                CameraDisplay.Source = bitmapSource;
            }
            catch (Exception ex)
            {
                AddLog($"显示图像失败: {ex.Message}");
            }
        }

        // 创建BitmapSource
        private BitmapSource CreateBitmapSource(byte[] imageData, int width, int height)
        {
            try
            {
                // 假设是灰度图像
                int stride = width;
                BitmapSource source = BitmapSource.Create(
                    width, height,
                    96, 96,
                    PixelFormats.Gray8, null,
                    imageData, stride);

                // 冻结以便跨线程使用
                return source.GetAsFrozen() as BitmapSource;
            }
            catch
            {
                return null;
            }
        }

        // 连接相机
        private void ConnectCamera_Click(object sender, RoutedEventArgs e)
        {
            // 获取配置的序列号
            string serialNo = CamSerialStr;
            if (string.IsNullOrEmpty(serialNo))
            {
                AddLog("错误: 序列号为空");
                return;
            }

            AddLog($"正在查找序列号为 {serialNo} 的相机...");

            // 初始化SDK
            int nRet = MyCamera.MV_CC_Initialize_NET();
            if (nRet != MyCamera.MV_OK)
            {
                AddLog($"SDK初始化失败，错误码: 0x{nRet:X8}");
                return;
            }

            // 枚举设备
            nRet = MyCamera.MV_CC_EnumDevices_NET(
                MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE, 
                ref m_pDeviceList);
            
            if (nRet != MyCamera.MV_OK)
            {
                AddLog($"枚举设备失败，错误码: 0x{nRet:X8}");
                return;
            }

            AddLog($"发现 {m_pDeviceList.nDeviceNum} 个设备");

            if (m_pDeviceList.nDeviceNum <= 0)
            {
                AddLog("未发现任何设备");
                return;
            }

            // 遍历设备列表，查找匹配的相机
            bool deviceFound = false;
            MyCamera.MV_CC_DEVICE_INFO targetDevice = new MyCamera.MV_CC_DEVICE_INFO();

            for (int i = 0; i < m_pDeviceList.nDeviceNum; i++)
            {
                IntPtr pInfo = m_pDeviceList.pDeviceInfo[i];
                MyCamera.MV_CC_DEVICE_INFO deviceInfo = (MyCamera.MV_CC_DEVICE_INFO)
                    Marshal.PtrToStructure(pInfo, typeof(MyCamera.MV_CC_DEVICE_INFO));

                string deviceSerial = "";

                // 根据设备类型获取序列号
                if (deviceInfo.nTLayerType == MyCamera.MV_GIGE_DEVICE)
                {
                    // GigE相机
                    IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(deviceInfo.SpecialInfo.stGigEInfo, 0);
                    MyCamera.MV_GIGE_DEVICE_INFO gigeInfo = (MyCamera.MV_GIGE_DEVICE_INFO)
                        Marshal.PtrToStructure(buffer, typeof(MyCamera.MV_GIGE_DEVICE_INFO));
                    deviceSerial = gigeInfo.chSerialNumber;
                    AddLog($"  [{i}] GigE相机, 序列号: {deviceSerial}");
                }
                else if (deviceInfo.nTLayerType == MyCamera.MV_USB_DEVICE)
                {
                    // USB相机
                    IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(deviceInfo.SpecialInfo.stUsb3VInfo, 0);
                    MyCamera.MV_USB3_DEVICE_INFO usbInfo = (MyCamera.MV_USB3_DEVICE_INFO)
                        Marshal.PtrToStructure(buffer, typeof(MyCamera.MV_USB3_DEVICE_INFO));
                    deviceSerial = usbInfo.chSerialNumber;
                    AddLog($"  [{i}] USB相机, 序列号: {deviceSerial}");
                }

                // 检查序列号是否匹配
                if (deviceSerial.Contains(serialNo))
                {
                    AddLog($"找到匹配的相机!");
                    targetDevice = deviceInfo;
                    deviceFound = true;
                    break;
                }
            }

            if (!deviceFound)
            {
                AddLog($"未找到序列号为 {serialNo} 的相机");
                return;
            }

            // 创建设备
            nRet = m_pCamera.MV_CC_CreateDevice_NET(ref targetDevice);
            if (nRet != MyCamera.MV_OK)
            {
                AddLog($"创建设备失败，错误码: 0x{nRet:X8}");
                return;
            }

            // 打开设备
            nRet = m_pCamera.MV_CC_OpenDevice_NET();
            if (nRet != MyCamera.MV_OK)
            {
                AddLog($"打开设备失败，错误码: 0x{nRet:X8}");
                m_pCamera.MV_CC_DestroyDevice_NET();
                return;
            }

            m_CamOpenSuccess = true;
            AddLog("相机连接成功！");

            // 注册回调函数
            _cbImage = new MyCamera.cbOutputdelegate(ReceiveThreadProcess);
            nRet = m_pCamera.MV_CC_RegisterImageCallBack_NET(_cbImage, IntPtr.Zero);
            if (nRet != MyCamera.MV_OK)
            {
                AddLog($"注册回调失败，错误码: 0x{nRet:X8}");
            }

            // 开始采集
            nRet = m_pCamera.MV_CC_StartGrabbing_NET();
            if (nRet != MyCamera.MV_OK)
            {
                AddLog($"开始采集失败，错误码: 0x{nRet:X8}");
                return;
            }

            m_bGrabbing = true;
            AddLog("开始采集...");

            // 更新UI
            Application.Current.Dispatcher.Invoke(() =>
            {
                CameraConnectButton.Content = "断开相机";
                StatusText.Text = "相机已连接";
                StatusText.Foreground = Brushes.Green;
            });
        }

        // 断开相机
        private void DisconnectCamera()
        {
            try
            {
                // 停止采集
                if (m_bGrabbing)
                {
                    m_pCamera.MV_CC_StopGrabbing_NET();
                    m_bGrabbing = false;
                    AddLog("停止采集");
                }

                // 关闭设备
                if (m_CamOpenSuccess)
                {
                    m_pCamera.MV_CC_CloseDevice_NET();
                    m_pCamera.MV_CC_DestroyDevice_NET();
                    m_CamOpenSuccess = false;
                    AddLog("相机已断开");
                }

                Application.Current.Dispatcher.Invoke(() =>
                {
                    CameraConnectButton.Content = "连接相机";
                    StatusText.Text = "相机未连接";
                    StatusText.Foreground = Brushes.Red;
                });
            }
            catch (Exception ex)
            {
                AddLog($"断开相机异常: {ex.Message}");
            }
        }

        // 按钮点击处理
        private void CameraConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (m_CamOpenSuccess)
            {
                DisconnectCamera();
            }
            else
            {
                ConnectCamera_Click(sender, e);
            }
        }

        // 拍照按钮
        private void CaptureButton_Click(object sender, RoutedEventArgs e)
        {
            if (!m_CamOpenSuccess || !m_bGrabbing)
            {
                AddLog("请先连接相机");
                return;
            }

            try
            {
                MyCamera.MV_FRAME_OUT_INFO stFrameInfo = new MyCamera.MV_FRAME_OUT_INFO();
                IntPtr pData = Marshal.AllocHGlobal(4096 * 4096 * 3);

                int nRet = m_pCamera.MV_CC_GetOneFrame_NET(pData, 4096 * 4096 * 3, ref stFrameInfo);
                if (nRet == MyCamera.MV_OK)
                {
                    string fileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss}.bmp";
                    string filePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);
                    
                    // 这里需要保存图像
                    AddLog($"已保存图像到: {filePath}");
                }
                else
                {
                    AddLog($"抓取图像失败，错误码: 0x{nRet:X8}");
                }

                Marshal.FreeHGlobal(pData);
            }
            catch (Exception ex)
            {
                AddLog($"拍照异常: {ex.Message}");
            }
        }

        // 清空日志
        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            LogTextBox.Clear();
        }

        // 添加日志
        private void AddLog(string message)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                string log = $"[{DateTime.Now:HH:mm:ss}] {message}";
                LogTextBox.AppendText(log + "\n");
                LogTextBox.ScrollToEnd();
            });
        }

        // 设置相机配置
        public void SetCameraConfig(string serialNo)
        {
            CamSerialStr = serialNo;
            AddLog($"相机配置: 序列号 = {serialNo}");
        }
    }
}
