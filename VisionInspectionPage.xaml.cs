using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.IO;
using System.Threading;
using MvCamCtrl.NET;

namespace WpfApp1
{
    public partial class VisionInspectionPage : Page
    {
        // 海康SDK相关
        private MyCamera m_pCamera = new MyCamera();
        private MyCamera.MV_CC_DEVICE_INFO_LIST m_stDeviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
        private MyCamera.cbOutputdelegate m_ImageCallback;
        private bool m_isGrabbing = false;
        private Thread m_hReceiveThread = null;
        private bool m_isOperationg = false;
        
        // 配置信息（从MainWindow获取）
        private string m_cameraSerial = "";
        private uint m_nDeviceType = 0;
        
        public VisionInspectionPage()
        {
            InitializeComponent();
        }
        
        // 从MainWindow获取相机配置
        public void SetCameraConfig(string serial, uint deviceType)
        {
            m_cameraSerial = serial;
            m_nDeviceType = deviceType;
            AddLog($"相机配置: 序列号={serial}, 类型={deviceType}");
        }
        
        // 添加日志
        private void AddLog(string message)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                TxtLog.Text += $"[{time}] {message}\r\n";
                TxtLog.ScrollToEnd();
            }));
        }
        
        // 开始采集按钮（连接相机并开始采集）
        private void StartGrabButton_Click(object sender, RoutedEventArgs e)
        {
            if (m_isOperationg)
            {
                AddLog("正在操作中，请稍候...");
                return;
            }
            
            if (m_isGrabbing)
            {
                AddLog("相机已在采集");
                return;
            }
            
            m_isOperationg = true;
            StartGrabButton.IsEnabled = false;
            
            new Thread(() =>
            {
                try
                {
                    // 连接相机
                    bool ret = ConnectCamera();
                    if (!ret)
                    {
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            StartGrabButton.IsEnabled = true;
                        }));
                        m_isOperationg = false;
                        return;
                    }
                    
                    // 注册回调
                    m_ImageCallback = new MyCamera.cbOutputdelegate(ImageCallback);
                    int nRet = m_pCamera.MV_CC_RegisterImageCallBack_NET(m_ImageCallback, IntPtr.Zero);
                    if (nRet != MyCamera.MV_OK)
                    {
                        AddLog($"注册回调失败: 0x{nRet:X}");
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            StartGrabButton.IsEnabled = true;
                        }));
                        m_isOperationg = false;
                        return;
                    }
                    
                    // 开始采集
                    nRet = m_pCamera.MV_CC_StartGrabbing_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        AddLog($"开始采集失败: 0x{nRet:X}");
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            StartGrabButton.IsEnabled = true;
                        }));
                        m_isOperationg = false;
                        return;
                    }
                    
                    m_isGrabbing = true;
                    AddLog("开始采集成功");
                    
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StartGrabButton.Content = "采集中...";
                        StartGrabButton.IsEnabled = true;
                        StopGrabButton.IsEnabled = true;
                    }));
                }
                catch (Exception ex)
                {
                    AddLog($"错误: {ex.Message}");
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StartGrabButton.IsEnabled = true;
                    }));
                }
                
                m_isOperationg = false;
            }).Start();
        }
        
        // 停止采集按钮（停止采集并断开相机）
        private void StopGrabButton_Click(object sender, RoutedEventArgs e)
        {
            if (m_isOperationg)
            {
                AddLog("正在操作中，请稍候...");
                return;
            }
            
            m_isOperationg = true;
            StopGrabButton.IsEnabled = false;
            
            new Thread(() =>
            {
                try
                {
                    // 停止采集
                    if (m_isGrabbing)
                    {
                        int nRet = m_pCamera.MV_CC_StopGrabbing_NET();
                        if (nRet != MyCamera.MV_OK)
                        {
                            AddLog($"停止采集失败: 0x{nRet:X}");
                        }
                        else
                        {
                            AddLog("停止采集成功");
                        }
                        m_isGrabbing = false;
                    }
                    
                    // 关闭设备
                    int nRet2 = m_pCamera.MV_CC_CloseDevice_NET();
                    if (nRet2 != MyCamera.MV_OK)
                    {
                        AddLog($"关闭设备失败: 0x{nRet2:X}");
                    }
                    else
                    {
                        AddLog("设备已断开");
                    }
                    
                    // 销毁设备
                    int nRet3 = m_pCamera.MV_CC_DestroyDevice_NET();
                    if (nRet3 != MyCamera.MV_OK)
                    {
                        AddLog($"销毁设备失败: 0x{nRet3:X}");
                    }
                    
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StartGrabButton.Content = "开始采集";
                        StopGrabButton.IsEnabled = true;
                    }));
                }
                catch (Exception ex)
                {
                    AddLog($"错误: {ex.Message}");
                }
                
                m_isOperationg = false;
            }).Start();
        }
        
        // 连接相机
        private bool ConnectCamera()
        {
            AddLog("正在查找相机...");
            
            // 枚举设备 - 支持GigE和USB
            int nRet = MyCamera.MV_CC_EnumDevices_NET(MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE, ref m_stDeviceList);
            if (nRet != MyCamera.MV_OK)
            {
                AddLog($"枚举设备失败: 0x{nRet:X}");
                return false;
            }
            
            AddLog($"发现 {m_stDeviceList.nDeviceNum} 个设备");
            
            if (m_stDeviceList.nDeviceNum == 0)
            {
                AddLog("未发现任何设备");
                return false;
            }
            
            // 遍历设备查找目标相机
            for (int i = 0; i < m_stDeviceList.nDeviceNum; i++)
            {
                IntPtr pDeviceInfo = Marshal.UnsafeAddrOfPinnedArrayElement(m_stDeviceList.pDeviceInfo, i);
                MyCamera.MV_CC_DEVICE_INFO device = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo, typeof(MyCamera.MV_CC_DEVICE_INFO));
                
                string serial = "";
                
                // 根据设备类型获取序列号
                if (device.nTLayerType == MyCamera.MV_GIGE_DEVICE)
                {
                    // GigE设备
                    IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(device.SpecialInfo.stGigEInfo, 0);
                    MyCamera.MV_GIGE_DEVICE_INFO gigeInfo = (MyCamera.MV_GIGE_DEVICE_INFO)Marshal.PtrToStructure(buffer, typeof(MyCamera.MV_GIGE_DEVICE_INFO));
                    serial = System.Text.Encoding.ASCII.GetString(gigeInfo.chSerialNumber).TrimEnd('\0');
                    AddLog($"  [{i}] GigE: {serial}");
                }
                else if (device.nTLayerType == MyCamera.MV_USB_DEVICE)
                {
                    // USB设备
                    IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(device.SpecialInfo.stUsb3VInfo, 0);
                    MyCamera.MV_USB3_DEVICE_INFO usbInfo = (MyCamera.MV_USB3_DEVICE_INFO)Marshal.PtrToStructure(buffer, typeof(MyCamera.MV_USB3_DEVICE_INFO));
                    serial = System.Text.Encoding.ASCII.GetString(usbInfo.chSerialNumber).TrimEnd('\0');
                    AddLog($"  [{i}] USB: {serial}");
                }
                
                // 匹配目标相机
                if (!string.IsNullOrEmpty(m_cameraSerial) && serial.Contains(m_cameraSerial))
                {
                    AddLog($"找到目标相机: {serial}");
                    
                    // 创建设备
                    nRet = m_pCamera.MV_CC_CreateDevice_NET(ref device);
                    if (nRet != MyCamera.MV_OK)
                    {
                        AddLog($"创建设备失败: 0x{nRet:X}");
                        return false;
                    }
                    
                    // 打开设备
                    nRet = m_pCamera.MV_CC_OpenDevice_NET(MyCamera.MV_ACCESS_Exclusive, 0);
                    if (nRet != MyCamera.MV_OK)
                    {
                        AddLog($"打开设备失败: 0x{nRet:X}");
                        m_pCamera.MV_CC_DestroyDevice_NET();
                        return false;
                    }
                    
                    AddLog("相机连接成功");
                    return true;
                }
            }
            
            // 如果没有配置序列号，连接第一个设备
            if (string.IsNullOrEmpty(m_cameraSerial))
            {
                AddLog("未配置序列号，连接第一个设备");
                
                IntPtr pDeviceInfo = Marshal.UnsafeAddrOfPinnedArrayElement(m_stDeviceList.pDeviceInfo, 0);
                MyCamera.MV_CC_DEVICE_INFO device = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo, typeof(MyCamera.MV_CC_DEVICE_INFO));
                
                nRet = m_pCamera.MV_CC_CreateDevice_NET(ref device);
                if (nRet != MyCamera.MV_OK)
                {
                    AddLog($"创建设备失败: 0x{nRet:X}");
                    return false;
                }
                
                nRet = m_pCamera.MV_CC_OpenDevice_NET(MyCamera.MV_ACCESS_Exclusive, 0);
                if (nRet != MyCamera.MV_OK)
                {
                    AddLog($"打开设备失败: 0x{nRet:X}");
                    m_pCamera.MV_CC_DestroyDevice_NET();
                    return false;
                }
                
                AddLog("相机连接成功");
                return true;
            }
            
            AddLog($"未找到序列号为 {m_cameraSerial} 的相机");
            return false;
        }
        
        // 图像回调函数
        private void ImageCallback(IntPtr pData, ref MyCamera.MV_FRAME_OUT_INFO pFrameInfo, IntPtr pUser)
        {
            try
            {
                int nWidth = (int)pFrameInfo.nWidth;
                int nHeight = (int)pFrameInfo.nHeight;
                int nFrameLen = (int)pFrameInfo.nFrameLen;
                uint enPixelType = pFrameInfo.enPixelType;
                
                // 更新帧计数
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    TxtFrameCount.Text = (Convert.ToInt32(TxtFrameCount.Text) + 1).ToString();
                }));
                
                // 复制图像数据
                byte[] pImageBuffer = new byte[nFrameLen];
                Marshal.Copy(pData, pImageBuffer, 0, (int)nFrameLen);
                
                // 显示图像
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    DisplayImage(pImageBuffer, nWidth, nHeight, enPixelType);
                }));
            }
            catch (Exception ex)
            {
                AddLog($"回调错误: {ex.Message}");
            }
        }
        
        // 显示图像
        private void DisplayImage(byte[] imageData, int nWidth, int nHeight, uint enPixelType)
        {
            try
            {
                // 根据像素格式创建Bitmap
                System.Drawing.Bitmap bmp = null;
                
                if (enPixelType == (uint)MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8)
                {
                    // 黑白图像
                    bmp = new System.Drawing.Bitmap(nWidth, nHeight, System.Drawing.Imaging.PixelFormat.Format8bppIndexed);
                    
                    // 设置灰度调色板
                    System.Drawing.Imaging.ColorPalette palette = bmp.Palette;
                    for (int i = 0; i < 256; i++)
                    {
                        palette.Entries[i] = System.Drawing.Color.FromArgb(i, i, i);
                    }
                    bmp.Palette = palette;
                    
                    // 复制图像数据
                    System.Drawing.Imaging.BitmapData bmpData = bmp.LockBits(
                        new System.Drawing.Rectangle(0, 0, nWidth, nHeight),
                        System.Drawing.Imaging.ImageLockMode.WriteOnly,
                        bmp.PixelFormat);
                    Marshal.Copy(imageData, 0, bmpData.Scan0, imageData.Length);
                    bmp.UnlockBits(bmpData);
                }
                else if (enPixelType == (uint)MyCamera.MvGvspPixelType.PixelType_Gvsp_RGB8)
                {
                    // RGB图像
                    bmp = new System.Drawing.Bitmap(nWidth, nHeight, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                    System.Drawing.Imaging.BitmapData bmpData = bmp.LockBits(
                        new System.Drawing.Rectangle(0, 0, nWidth, nHeight),
                        System.Drawing.Imaging.ImageLockMode.WriteOnly,
                        bmp.PixelFormat);
                    Marshal.Copy(imageData, 0, bmpData.Scan0, imageData.Length);
                    bmp.UnlockBits(bmpData);
                }
                
                if (bmp != null)
                {
                    // 显示到Image控件
                    CameraDisplay.Source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        bmp.GetHbitmap(),
                        IntPtr.Zero,
                        Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    
                    bmp.Dispose();
                }
            }
            catch (Exception ex)
            {
                AddLog($"显示图像错误: {ex.Message}");
            }
        }
        
        // 保存图像
        private void SaveImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (!m_isGrabbing || CameraDisplay.Source == null)
            {
                AddLog("请先开始采集");
                return;
            }
            
            try
            {
                var bitmapSource = (System.Windows.Media.Imaging.BitmapSource)CameraDisplay.Source;
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmapSource));
                
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), 
                    $"Image_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                
                using (var stream = new FileStream(path, FileMode.Create))
                {
                    encoder.Save(stream);
                }
                
                AddLog($"图像已保存: {path}");
            }
            catch (Exception ex)
            {
                AddLog($"保存图像失败: {ex.Message}");
            }
        }
        
        // 清空日志
        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            TxtLog.Text = "";
        }
        
        // 页面加载
        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            AddLog("页面加载完成");
        }
        
        // 页面卸载
        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            // 确保停止采集并关闭设备
            if (m_isGrabbing)
            {
                m_pCamera.MV_CC_StopGrabbing_NET();
                m_isGrabbing = false;
            }
            
            m_pCamera.MV_CC_CloseDevice_NET();
            m_pCamera.MV_CC_DestroyDevice_NET();
        }
    }
}
