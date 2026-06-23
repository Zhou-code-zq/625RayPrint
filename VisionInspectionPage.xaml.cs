using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using MvCamCtrl.NET;

namespace WpfApp1
{
    public partial class VisionInspectionPage : UserControl
    {
        // 海康相机对象
        private MyCamera m_pMyCamera = new MyCamera();
        
        // 操作锁，防止重复操作
        private bool m_isOperating = false;
        
        // 相机配置
        private string m_deviceType = "";
        private string m_deviceSerial = "";
        private string m_deviceIp = "";
        
        // 帧计数
        private int m_frameCount = 0;
        
        // 显示帧计数
        private int m_displayFrameCount = 0;
        
        // 设备列表
        private MyCamera.MV_CC_DEVICE_INFO_LIST m_stDeviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
        
        public VisionInspectionPage()
        {
            InitializeComponent();
            LogTextBox.Text = "";
        }
        
        // 日志输出
        private void Log(string message)
        {
            string logMessage = $"[{DateTime.Now:HH:mm:ss}] {message}";
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LogTextBox.AppendText(logMessage + "\n");
                LogTextBox.ScrollToEnd();
            }));
        }
        
        // 连接相机并开始采集
        private void StartGrabButton_Click(object sender, RoutedEventArgs e)
        {
            if (m_isOperating)
            {
                Log("操作进行中，请稍候...");
                return;
            }
            
            if (m_pMyCamera != null && m_pMyCamera.MV_CC_IsDeviceConnected_NET())
            {
                Log("相机已连接");
                return;
            }
            
            m_isOperating = true;
            StartGrabButton.IsEnabled = false;
            StartGrabButton.Content = "连接中...";
            Log("开始连接相机...");
            
            new Thread(() =>
            {
                try
                {
                    // 获取配置
                    string deviceType = "", deviceSerial = "", deviceIp = "";
                    Application.Current.Dispatcher.Invoke(new Action(() =>
                    {
                        var mainWindow = Application.Current.MainWindow as MainWindow;
                        if (mainWindow != null)
                        {
                            deviceType = mainWindow.CameraDeviceType;
                            deviceSerial = mainWindow.CameraSerial;
                            deviceIp = mainWindow.CameraIp;
                        }
                    }));
                    
                    // 枚举设备
                    int nRet = MyCamera.MV_CC_EnumDevices_NET(MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE, ref m_stDeviceList);
                    if (nRet != MyCamera.MV_OK)
                    {
                        Log($"枚举设备失败，错误码: {nRet}");
                        goto cleanup;
                    }
                    
                    if (m_stDeviceList.nDeviceNum == 0)
                    {
                        Log("未发现任何设备");
                        goto cleanup;
                    }
                    
                    Log($"发现 {m_stDeviceList.nDeviceNum} 个设备");
                    
                    // 查找匹配的设备
                    IntPtr pDeviceInfo = IntPtr.Zero;
                    bool foundDevice = false;
                    
                    for (uint i = 0; i < m_stDeviceList.nDeviceNum; i++)
                    {
                        pDeviceInfo = Marshal.UnsafeAddrOfPinnedArrayElement(m_stDeviceList.pDeviceInfo, (int)i);
                        if (pDeviceInfo == IntPtr.Zero) continue;
                        
                        MyCamera.MV_CC_DEVICE_INFO deviceInfo = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo, typeof(MyCamera.MV_CC_DEVICE_INFO));
                        
                        // 获取设备信息
                        string serial = GetDeviceSerial(deviceInfo);
                        uint deviceType = deviceInfo.nDeviceType;
                        
                        Log($"设备 {i}: 类型={deviceType}, 序列号={serial}");
                        
                        // 根据配置查找设备
                        bool match = false;
                        if (!string.IsNullOrEmpty(deviceSerial) && serial == deviceSerial)
                        {
                            match = true;
                        }
                        else if (!string.IsNullOrEmpty(deviceIp) && deviceType == MyCamera.MV_GIGE_DEVICE)
                        {
                            string ip = GetDeviceIp(deviceInfo);
                            if (ip == deviceIp)
                            {
                                match = true;
                            }
                        }
                        
                        if (match)
                        {
                            foundDevice = true;
                            break;
                        }
                    }
                    
                    if (!foundDevice)
                    {
                        Log($"未找到匹配的设备 (序列号: {deviceSerial}, IP: {deviceIp})");
                        goto cleanup;
                    }
                    
                    // 创建设备
                    nRet = m_pMyCamera.MV_CC_CreateDevice_NET(ref deviceInfo);
                    if (nRet != MyCamera.MV_OK)
                    {
                        Log($"创建设备失败，错误码: {nRet}");
                        goto cleanup;
                    }
                    
                    // 打开设备
                    nRet = m_pMyCamera.MV_CC_OpenDevice_NET(MyCamera.MV_ACCESS_Exclusive, 0);
                    if (nRet != MyCamera.MV_OK)
                    {
                        Log($"打开设备失败，错误码: {nRet}");
                        m_pMyCamera.MV_CC_DestroyDevice_NET();
                        goto cleanup;
                    }
                    
                    // 开始采集
                    nRet = m_pMyCamera.MV_CC_StartGrabbing_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        Log($"开始采集失败，错误码: {nRet}");
                        m_pMyCamera.MV_CC_CloseDevice_NET();
                        m_pMyCamera.MV_CC_DestroyDevice_NET();
                        goto cleanup;
                    }
                    
                    Log("相机连接成功，开始采集");
                    
                    // 启动图像采集线程
                    Thread captureThread = new Thread(GrabImageThread);
                    captureThread.IsBackground = true;
                    captureThread.Start();
                    
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StartGrabButton.Content = "采集中...";
                        StopGrabButton.IsEnabled = true;
                    }));
                    
                    cleanup:
                    ;
                }
                catch (Exception ex)
                {
                    Log($"异常: {ex.Message}");
                }
                finally
                {
                    m_isOperating = false;
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StartGrabButton.IsEnabled = true;
                        if (!(StartGrabButton.Content.ToString() == "采集中..."))
                        {
                            StartGrabButton.Content = "开始采集";
                        }
                    }));
                }
            }).Start();
        }
        
        // 停止采集并断开相机
        private void StopGrabButton_Click(object sender, RoutedEventArgs e)
        {
            if (m_isOperating)
            {
                Log("操作进行中，请稍候...");
                return;
            }
            
            if (m_pMyCamera == null || !m_pMyCamera.MV_CC_IsDeviceConnected_NET())
            {
                Log("相机未连接");
                return;
            }
            
            m_isOperating = true;
            StopGrabButton.IsEnabled = false;
            StopGrabButton.Content = "断开中...";
            Log("正在停止采集...");
            
            new Thread(() =>
            {
                try
                {
                    // 停止采集
                    int nRet = m_pMyCamera.MV_CC_StopGrabbing_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        Log($"停止采集失败，错误码: {nRet}");
                    }
                    
                    // 关闭设备
                    nRet = m_pMyCamera.MV_CC_CloseDevice_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        Log($"关闭设备失败，错误码: {nRet}");
                    }
                    
                    // 销毁设备
                    nRet = m_pMyCamera.MV_CC_DestroyDevice_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        Log($"销毁设备失败，错误码: {nRet}");
                    }
                    
                    Log("相机已断开");
                    m_frameCount = 0;
                    m_displayFrameCount = 0;
                    
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        FrameCountText.Text = "0";
                        StartGrabButton.Content = "开始采集";
                        StartGrabButton.IsEnabled = true;
                    }));
                }
                catch (Exception ex)
                {
                    Log($"异常: {ex.Message}");
                }
                finally
                {
                    m_isOperating = false;
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StopGrabButton.Content = "停止采集";
                    }));
                }
            }).Start();
        }
        
        // 采集图像线程
        private void GrabImageThread()
        {
            MyCamera.MV_FRAME_OUT stFrameOut = new MyCamera.MV_FRAME_OUT();
            int nRet = MyCamera.MV_OK;
            
            while (true)
            {
                // 检查相机是否还连接
                if (m_pMyCamera == null || !m_pMyCamera.MV_CC_IsDeviceConnected_NET())
                {
                    break;
                }
                
                // 获取一帧图像
                nRet = m_pMyCamera.MV_CC_GetOneFrameTimeout_NET(ref stFrameOut, 1000);
                if (nRet == MyCamera.MV_OK)
                {
                    m_frameCount++;
                    
                    // 显示图像
                    if (stFrameOut.pImageAddr != IntPtr.Zero && stFrameOut.nFrameLen > 0)
                    {
                        try
                        {
                            ShowImage(stFrameOut);
                        }
                        catch (Exception ex)
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                Log($"显示图像异常: {ex.Message}");
                            }));
                        }
                    }
                    
                    // 释放图像缓存
                    m_pMyCamera.MV_CC_FreeImageBuffer_NET(ref stFrameOut);
                }
                
                // 更新帧计数显示
                if (m_frameCount != m_displayFrameCount)
                {
                    m_displayFrameCount = m_frameCount;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        FrameCountText.Text = m_displayFrameCount.ToString();
                    }));
                }
            }
        }
        
        // 显示图像
        private void ShowImage(MyCamera.MV_FRAME_OUT stFrameOut)
        {
            int nWidth = (int)stFrameOut.nWidth;
            int nHeight = (int)stFrameOut.nHeight;
            IntPtr pImageAddr = stFrameOut.pImageAddr;
            uint nFrameLen = stFrameOut.nFrameLen;
            uint enPixelType = stFrameOut.enPixelType;
            
            if (nWidth <= 0 || nHeight <= 0 || pImageAddr == IntPtr.Zero)
            {
                return;
            }
            
            BitmapSource bitmapSource = null;
            
            // 根据像素格式创建BitmapSource
            if (enPixelType == MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8 ||
                enPixelType == MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono16)
            {
                // 黑白图像
                bitmapSource = BitmapSource.Create(nWidth, nHeight, 96, 96,
                    PixelFormats.Gray8, null, pImageAddr, (int)nFrameLen, nWidth);
            }
            else
            {
                // 彩色图像（假设为BGR8）
                int stride = nWidth * 3;
                byte[] imageData = new byte[nFrameLen];
                Marshal.Copy(pImageAddr, imageData, 0, (int)nFrameLen);
                
                // 转换为RGB
                byte[] rgbData = new byte[nWidth * nHeight * 3];
                for (int i = 0; i < nWidth * nHeight; i++)
                {
                    rgbData[i * 3] = imageData[i * 3 + 2];     // R
                    rgbData[i * 3 + 1] = imageData[i * 3 + 1]; // G
                    rgbData[i * 3 + 2] = imageData[i * 3];     // B
                }
                
                bitmapSource = BitmapSource.Create(nWidth, nHeight, 96, 96,
                    PixelFormats.Rgb24, null, rgbData, stride);
            }
            
            if (bitmapSource != null)
            {
                bitmapSource.Freeze();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    CameraDisplay.Source = bitmapSource;
                }));
            }
        }
        
        // 保存图像
        private void SaveImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (CameraDisplay.Source == null)
            {
                Log("没有图像可保存");
                return;
            }
            
            try
            {
                string fileName = $"Image_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);
                
                BitmapSource bitmapSource = CameraDisplay.Source as BitmapSource;
                if (bitmapSource != null)
                {
                    using (FileStream fs = new FileStream(path, FileMode.Create))
                    {
                        BitmapEncoder encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
                        encoder.Save(fs);
                    }
                    Log($"图像已保存: {path}");
                }
            }
            catch (Exception ex)
            {
                Log($"保存图像失败: {ex.Message}");
            }
        }
        
        // 清空日志
        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            LogTextBox.Clear();
        }
        
        // 获取设备序列号
        private string GetDeviceSerial(MyCamera.MV_CC_DEVICE_INFO deviceInfo)
        {
            try
            {
                // GigE设备
                if (deviceInfo.nDeviceType == MyCamera.MV_GIGE_DEVICE || 
                    deviceInfo.nDeviceType == MyCamera.MV_GENTL_GIGE_DEVICE ||
                    deviceInfo.nDeviceType == MyCamera.MV_VIR_GIGE_DEVICE)
                {
                    IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(deviceInfo.SpecialInfo.stGigEInfo, 0);
                    MyCamera.MV_GIGE_DEVICE_INFO gigeInfo = (MyCamera.MV_GIGE_DEVICE_INFO)Marshal.PtrToStructure(buffer, typeof(MyCamera.MV_GIGE_DEVICE_INFO));
                    return System.Text.Encoding.ASCII.GetString(gigeInfo.chSerialNumber).TrimEnd('\0');
                }
                // USB设备
                else if (deviceInfo.nDeviceType == MyCamera.MV_USB_DEVICE)
                {
                    IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(deviceInfo.SpecialInfo.stUsb3VInfo, 0);
                    MyCamera.MV_USB3_DEVICE_INFO usbInfo = (MyCamera.MV_USB3_DEVICE_INFO)Marshal.PtrToStructure(buffer, typeof(MyCamera.MV_USB3_DEVICE_INFO));
                    return System.Text.Encoding.ASCII.GetString(usbInfo.chSerialNumber).TrimEnd('\0');
                }
            }
            catch
            {
            }
            return "";
        }
        
        // 获取设备IP
        private string GetDeviceIp(MyCamera.MV_CC_DEVICE_INFO deviceInfo)
        {
            try
            {
                if (deviceInfo.nDeviceType == MyCamera.MV_GIGE_DEVICE ||
                    deviceInfo.nDeviceType == MyCamera.MV_GENTL_GIGE_DEVICE ||
                    deviceInfo.nDeviceType == MyCamera.MV_VIR_GIGE_DEVICE)
                {
                    IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(deviceInfo.SpecialInfo.stGigEInfo, 0);
                    MyCamera.MV_GIGE_DEVICE_INFO gigeInfo = (MyCamera.MV_GIGE_DEVICE_INFO)Marshal.PtrToStructure(buffer, typeof(MyCamera.MV_GIGE_DEVICE_INFO));
                    
                    // 转换IP地址
                    byte[] ipBytes = BitConverter.GetBytes(gigeInfo.nCurrentIp);
                    return $"{ipBytes[0]}.{ipBytes[1]}.{ipBytes[2]}.{ipBytes[3]}";
                }
            }
            catch
            {
            }
            return "";
        }
        
        // 页面加载
        private void VisionInspectionPage_Loaded(object sender, RoutedEventArgs e)
        {
            Log("视觉检测页面已加载");
            Log("请配置相机参数后点击\"开始采集\"连接相机");
        }
        
        // 页面卸载
        private void VisionInspectionPage_Unloaded(object sender, RoutedEventArgs e)
        {
            // 停止采集
            if (m_pMyCamera != null && m_pMyCamera.MV_CC_IsDeviceConnected_NET())
            {
                m_pMyCamera.MV_CC_StopGrabbing_NET();
                m_pMyCamera.MV_CC_CloseDevice_NET();
                m_pMyCamera.MV_CC_DestroyDevice_NET();
            }
        }
    }
}
