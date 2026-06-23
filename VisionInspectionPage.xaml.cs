using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using MvCamCtrl.NET;

namespace WpfApp1
{
    public partial class VisionInspectionPage : Page
    {
        // 相机对象
        private MyCamera m_pCamera = new MyCamera();
        
        // 相机配置
        private string m_deviceSerial = "";
        
        // 线程控制
        private Thread m_hReceiveThread = null;
        private bool m_isGrabbing = false;
        
        // 帧计数
        private int m_nFrameCount = 0;
        private object m_frameLock = new object();
        
        // 回调委托
        private MyCamera.cbOutputdelegate m_ImageCallback;
        
        // 缓存最新图像数据
        private byte[] m_pImageBuf = null;
        private int m_nImageWidth = 0;
        private int m_nImageHeight = 0;

        public VisionInspectionPage()
        {
            InitializeComponent();
        }

        // 设置相机配置（从MainWindow调用）
        public void SetCameraConfig(string serialNo, uint deviceType)
        {
            m_deviceSerial = serialNo;
            Application.Current.Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText($"[配置] 序列号: {serialNo}, 类型: {deviceType}\n");
                LogTextBox.ScrollToEnd();
            });
        }

        // 开始采集按钮
        private void StartCameraButton_Click(object sender, RoutedEventArgs e)
        {
            if (m_isGrabbing)
            {
                // 停止采集
                StopGrabbing();
            }
            else
            {
                // 开始采集
                StartGrabbing();
            }
        }

        // 保存图像按钮
        private void SaveImageButton_Click(object sender, RoutedEventArgs e)
        {
            SaveCurrentImage();
        }

        // 清空日志按钮
        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            LogTextBox.Clear();
        }

        // 开始采集
        private void StartGrabbing()
        {
            try
            {
                Log("开始连接相机...");
                
                // 初始化SDK
                int nRet = MyCamera.MV_CC_Initialize_NET();
                if (MyCamera.MV_OK != nRet)
                {
                    Log($"SDK初始化失败，错误码: 0x{nRet:X}");
                    return;
                }
                
                // 枚举设备
                MyCamera.MV_CC_DEVICE_INFO_LIST stDeviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
                nRet = MyCamera.MV_CC_EnumDevices_NET(MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE, ref stDeviceList);
                if (MyCamera.MV_OK != nRet)
                {
                    Log($"枚举设备失败，错误码: 0x{nRet:X}");
                    return;
                }
                
                Log($"发现 {stDeviceList.nDeviceNum} 个设备");
                
                if (stDeviceList.nDeviceNum == 0)
                {
                    Log("未发现任何设备");
                    return;
                }
                
                // 查找指定序列号的设备
                IntPtr pDeviceInfo = IntPtr.Zero;
                for (int i = 0; i < (int)stDeviceList.nDeviceNum; i++)
                {
                    pDeviceInfo = Marshal.AddrOfPinnedArrayElement(stDeviceList.pDeviceInfo, i);
                    MyCamera.MV_CC_DEVICE_INFO stDeviceInfo = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo, typeof(MyCamera.MV_CC_DEVICE_INFO));
                    
                    string serial = GetDeviceSerial(stDeviceInfo);
                    Log($"设备 {i}: 序列号 = {serial}");
                    
                    if (!string.IsNullOrEmpty(m_deviceSerial) && serial == m_deviceSerial)
                    {
                        Log($"找到目标设备: {serial}");
                        break;
                    }
                }
                
                // 创建设备
                nRet = m_pCamera.MV_CC_CreateDevice_NET(ref stDeviceList.pDeviceInfo[0]);
                if (MyCamera.MV_OK != nRet)
                {
                    Log($"创建设备失败，错误码: 0x{nRet:X}");
                    return;
                }
                
                // 打开设备
                nRet = m_pCamera.MV_CC_OpenDevice_NET(MyCamera.MV_ACCESS_Exclusive, 0);
                if (MyCamera.MV_OK != nRet)
                {
                    Log($"打开设备失败，错误码: 0x{nRet:X}");
                    m_pCamera.MV_CC_DestroyDevice_NET();
                    return;
                }
                
                // 获取图像宽度
                int nWidth = 0;
                int nPayloadSize = 0;
                MyCamera.MVCC_INTVALUE stParam = new MyCamera.MVCC_INTVALUE();
                nRet = m_pCamera.MV_CC_GetIntValue_NET("Width", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    nWidth = (int)stParam.nCurValue;
                    m_nImageWidth = nWidth;
                    Log($"图像宽度: {nWidth}");
                }
                
                // 获取图像高度
                nRet = m_pCamera.MV_CC_GetIntValue_NET("Height", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    m_nImageHeight = (int)stParam.nCurValue;
                    Log($"图像高度: {m_nImageHeight}");
                }
                
                // 获取payload size
                nRet = m_pCamera.MV_CC_GetIntValue_NET("PayloadSize", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    nPayloadSize = (int)stParam.nCurValue;
                    Log($"PayloadSize: {nPayloadSize}");
                }
                
                // 设置触发模式为连续采集
                nRet = m_pCamera.MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                
                // 开始采集
                nRet = m_pCamera.MV_CC_StartGrabbing_NET();
                if (MyCamera.MV_OK != nRet)
                {
                    Log($"开始采集失败，错误码: 0x{nRet:X}");
                    m_pCamera.MV_CC_CloseDevice_NET();
                    m_pCamera.MV_CC_DestroyDevice_NET();
                    return;
                }
                
                // 注册回调
                m_ImageCallback = new MyCamera.cbOutputdelegate(ImageCallback);
                nRet = m_pCamera.MV_CC_RegisterImageCallBack_NET(m_ImageCallback, IntPtr.Zero);
                
                // 分配图像缓冲区
                if (nPayloadSize > 0)
                {
                    m_pImageBuf = new byte[nPayloadSize];
                }
                
                m_isGrabbing = true;
                m_nFrameCount = 0;
                
                // 更新UI
                Dispatcher.Invoke(() =>
                {
                    StartCameraButton.Content = "停止采集";
                    StatusText.Text = "采集中";
                    StatusText.Foreground = System.Windows.Media.Brushes.Green;
                });
                
                Log("相机连接成功，开始采集");
            }
            catch (Exception ex)
            {
                Log($"异常: {ex.Message}");
            }
        }

        // 停止采集
        private void StopGrabbing()
        {
            try
            {
                m_isGrabbing = false;
                
                // 停止采集
                m_pCamera.MV_CC_StopGrabbing_NET();
                
                // 关闭设备
                m_pCamera.MV_CC_CloseDevice_NET();
                
                // 销毁设备
                m_pCamera.MV_CC_DestroyDevice_NET();
                
                // 关闭SDK
                MyCamera.MV_CC_Finalize_NET();
                
                // 更新UI
                Dispatcher.Invoke(() =>
                {
                    StartCameraButton.Content = "开始采集";
                    StatusText.Text = "已停止";
                    StatusText.Foreground = System.Windows.Media.Brushes.Gray;
                    CameraImage.Source = null;
                });
                
                Log("相机已断开");
            }
            catch (Exception ex)
            {
                Log($"异常: {ex.Message}");
            }
        }

        // 图像回调
        private void ImageCallback(IntPtr pData, ref MyCamera.MV_FRAME_OUT pFrameInfo, IntPtr pUser)
        {
            try
            {
                // 复制图像数据
                int nDataLen = (int)pFrameInfo.nFrameLen;
                if (nDataLen > 0 && m_pImageBuf != null)
                {
                    Marshal.Copy(pData, m_pImageBuf, 0, nDataLen);
                    
                    // 更新帧计数
                    lock (m_frameLock)
                    {
                        m_nFrameCount++;
                    }
                    
                    // 显示图像
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        DisplayImage((int)pFrameInfo.nWidth, (int)pFrameInfo.nHeight, m_pImageBuf, (uint)pFrameInfo.enPixelType);
                        FrameCountText.Text = m_nFrameCount.ToString();
                    }));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"回调异常: {ex.Message}");
            }
        }

        // 显示图像
        private void DisplayImage(int nWidth, int nHeight, byte[] pImageBuf, uint enPixelType)
        {
            try
            {
                BitmapSource bitmap = null;
                
                // 判断像素格式
                if (enPixelType == (uint)MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8)
                {
                    // 黑白图像
                    bitmap = BitmapSource.Create(nWidth, nHeight, 96, 96,
                        System.Windows.Media.PixelFormats.Gray8, null, pImageBuf, nWidth);
                }
                else
                {
                    // 彩色图像 - 假设RGB8
                    byte[] rgbBuf = ConvertBGRToRGB(pImageBuf, nWidth * nHeight * 3);
                    bitmap = BitmapSource.Create(nWidth, nHeight, 96, 96,
                        System.Windows.Media.PixelFormats.Rgb24, null, rgbBuf, nWidth * 3);
                }
                
                if (bitmap != null)
                {
                    bitmap.Freeze();
                    CameraImage.Source = bitmap;
                }
            }
            catch (Exception ex)
            {
                Log($"显示图像异常: {ex.Message}");
            }
        }

        // BGR转RGB
        private byte[] ConvertBGRToRGB(byte[] bgrBuf, int bufLen)
        {
            byte[] rgbBuf = new byte[bufLen];
            for (int i = 0; i < bufLen / 3; i++)
            {
                rgbBuf[i * 3] = bgrBuf[i * 3 + 2];
                rgbBuf[i * 3 + 1] = bgrBuf[i * 3 + 1];
                rgbBuf[i * 3 + 2] = bgrBuf[i * 3];
            }
            return rgbBuf;
        }

        // 保存当前图像
        private void SaveCurrentImage()
        {
            try
            {
                if (m_pImageBuf == null || m_nImageWidth == 0 || m_nImageHeight == 0)
                {
                    Log("没有可保存的图像");
                    return;
                }
                
                string folder = AppDomain.CurrentDomain.BaseDirectory + "Images";
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }
                
                string filename = folder + $"\\image_{DateTime.Now:yyyyMMdd_HHmmss}.bmp";
                
                // 保存为BMP格式
                using (FileStream fs = new FileStream(filename, FileMode.Create))
                {
                    // BMP文件头
                    byte[] header = new byte[54];
                    header[0] = 0x42; header[1] = 0x4D; // "BM"
                    int fileSize = 54 + m_pImageBuf.Length;
                    header[2] = (byte)(fileSize & 0xFF);
                    header[3] = (byte)((fileSize >> 8) & 0xFF);
                    header[4] = (byte)((fileSize >> 16) & 0xFF);
                    header[5] = (byte)((fileSize >> 24) & 0xFF);
                    header[10] = 54; // 数据偏移
                    
                    // DIB头
                    header[14] = 40; // DIB头大小
                    header[18] = (byte)(m_nImageWidth & 0xFF);
                    header[19] = (byte)((m_nImageWidth >> 8) & 0xFF);
                    header[22] = (byte)(m_nImageHeight & 0xFF);
                    header[23] = (byte)((m_nImageHeight >> 8) & 0xFF);
                    header[26] = 1; // 平面数
                    header[28] = 8; // 每像素位数
                    
                    fs.Write(header, 0, 54);
                    fs.Write(m_pImageBuf, 0, m_pImageBuf.Length);
                }
                
                Log($"图像已保存: {filename}");
            }
            catch (Exception ex)
            {
                Log($"保存图像异常: {ex.Message}");
            }
        }

        // 获取设备序列号
        private string GetDeviceSerial(MyCamera.MV_CC_DEVICE_INFO stDeviceInfo)
        {
            try
            {
                if (stDeviceInfo.nTLayerType == MyCamera.MV_GIGE_DEVICE)
                {
                    IntPtr pGigEInfo = Marshal.AddrOfPinnedArrayElement(stDeviceInfo.SpecialInfo.stGigEInfo, 0);
                    MyCamera.MV_GIGE_DEVICE_INFO stGigEInfo = (MyCamera.MV_GIGE_DEVICE_INFO)Marshal.PtrToStructure(pGigEInfo, typeof(MyCamera.MV_GIGE_DEVICE_INFO));
                    return System.Text.Encoding.ASCII.GetString(stGigEInfo.chSerialNumber).TrimEnd('\0');
                }
                else if (stDeviceInfo.nTLayerType == MyCamera.MV_USB_DEVICE)
                {
                    IntPtr pUsbInfo = Marshal.AddrOfPinnedArrayElement(stDeviceInfo.SpecialInfo.stUsb3VInfo, 0);
                    MyCamera.MV_USB3_DEVICE_INFO stUsbInfo = (MyCamera.MV_USB3_DEVICE_INFO)Marshal.PtrToStructure(pUsbInfo, typeof(MyCamera.MV_USB3_DEVICE_INFO));
                    return System.Text.Encoding.ASCII.GetString(stUsbInfo.chSerialNumber).TrimEnd('\0');
                }
            }
            catch { }
            return "";
        }

        // 日志输出
        private void Log(string msg)
        {
            string logMsg = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText(logMsg + "\n");
                LogTextBox.ScrollToEnd();
            });
        }
    }
}
