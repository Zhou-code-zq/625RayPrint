using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.IO;
using System.Text;
using MvCamCtrl.NET;
using System.Windows.Threading;

namespace WpfApp1
{
    public partial class VisionInspectionPage : UserControl
    {
        // 相机对象
        MyCamera m_pDevice;
        
        // 相机状态
        bool m_isGrabbing = false;
        bool m_isConnected = false;
        
        // 帧计数
        int m_nFrameCount = 0;
        
        // 保存图像的锁
        private readonly object m_saveLock = new object();
        
        // 写日志到UI
        private void Log(string msg)
        {
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
                LogTextBox.ScrollToEnd();
            });
        }

        // 页面加载
        private void VisionInspectionPage_Loaded(object sender, RoutedEventArgs e)
        {
            // 初始化SDK
            int nRet = MyCamera.MV_CC_Initialize_NET();
            if (nRet != MyCamera.MV_OK)
            {
                Log($"SDK初始化失败，错误码：{nRet:X8}");
            }
            else
            {
                Log("SDK初始化成功");
            }
        }

        // 页面卸载
        private void VisionInspectionPage_Unloaded(object sender, RoutedEventArgs e)
        {
            // 停止采集
            if (m_isGrabbing)
            {
                m_pDevice.MV_CC_StopGrabbing_NET();
                m_isGrabbing = false;
            }
            
            // 关闭设备
            if (m_isConnected)
            {
                m_pDevice.MV_CC_CloseDevice_NET();
                m_isConnected = false;
            }
            
            // 销毁SDK
            MyCamera.MV_CC_Initialize_NET();
            
            Log("相机已释放");
        }

        // 按钮状态更新
        private void UpdateButtonState(bool connected, bool grabbing)
        {
            Dispatcher.Invoke(() =>
            {
                CameraConnectButton.IsEnabled = !grabbing;
                StartGrabButton.IsEnabled = connected && !grabbing;
                StopGrabButton.IsEnabled = grabbing;
                CaptureButton.IsEnabled = connected;
            });
        }

        // 连接相机按钮
        private void CameraConnectButton_Click(object sender, RoutedEventArgs e)
        {
            // 从参数配置获取相机信息
            string serial = "";
            uint deviceType = 0;
            
            try
            {
                var mainWindow = Window.GetWindow(this) as MainWindow;
                if (mainWindow != null)
                {
                    serial = mainWindow.CameraSerial;
                    deviceType = mainWindow.CameraDeviceType;
                }
            }
            catch (Exception ex)
            {
                Log($"获取相机配置失败：{ex.Message}");
                return;
            }

            if (string.IsNullOrEmpty(serial))
            {
                Log("请先在参数配置页面设置相机序列号");
                return;
            }

            Log($"正在连接序列号为 {serial} 的相机...");

            // 枚举设备
            MyCamera.MV_CC_DEVICE_INFO_LIST stDeviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
            uint nType = MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE;
            int nRet = MyCamera.MV_CC_EnumDevices_NET(nType, ref stDeviceList);
            if (nRet != MyCamera.MV_OK)
            {
                Log($"枚举设备失败，错误码：{nRet:X8}");
                return;
            }

            Log($"发现 {stDeviceList.nDeviceNum} 个设备");

            // 查找目标设备
            IntPtr pDeviceInfo = IntPtr.Zero;
            for (ushort i = 0; i < stDeviceList.nDeviceNum; i++)
            {
                pDeviceInfo = Marshal.UnsafeAddrOfPinnedArrayElement(stDeviceList.pDeviceInfo, i);
                MyCamera.MV_CC_DEVICE_INFO device = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo, typeof(MyCamera.MV_CC_DEVICE_INFO));

                // 获取序列号
                string deviceSerial = "";
                if (device.nTLayerType == MyCamera.MV_GIGE_DEVICE)
                {
                    IntPtr gigePtr = Marshal.UnsafeAddrOfPinnedArrayElement(device.SpecialInfo.stGigEInfo, 0);
                    MyCamera.MV_GIGE_DEVICE_INFO gigeInfo = (MyCamera.MV_GIGE_DEVICE_INFO)Marshal.PtrToStructure(gigePtr, typeof(MyCamera.MV_GIGE_DEVICE_INFO));
                    deviceSerial = Encoding.ASCII.GetString(gigeInfo.chSerialNumber).TrimEnd('\0');
                }
                else if (device.nTLayerType == MyCamera.MV_USB_DEVICE)
                {
                    IntPtr usbPtr = Marshal.UnsafeAddrOfPinnedArrayElement(device.SpecialInfo.stUsb3VInfo, 0);
                    MyCamera.MV_USB3_DEVICE_INFO usbInfo = (MyCamera.MV_USB3_DEVICE_INFO)Marshal.PtrToStructure(usbPtr, typeof(MyCamera.MV_USB3_DEVICE_INFO));
                    deviceSerial = Encoding.ASCII.GetString(usbInfo.chSerialNumber).TrimEnd('\0');
                }

                Log($"设备 {i}: 序列号={deviceSerial}");

                if (deviceSerial == serial)
                {
                    Log($"找到目标相机！");
                    break;
                }
            }

            if (pDeviceInfo == IntPtr.Zero)
            {
                Log($"未找到序列号为 {serial} 的相机");
                return;
            }

            // 创建相机实例
            m_pDevice = new MyCamera();
            nRet = m_pDevice.MV_CC_CreateDevice_NET(ref device);
            if (nRet != MyCamera.MV_OK)
            {
                Log($"创建设备失败，错误码：{nRet:X8}");
                return;
            }

            // 打开设备
            nRet = m_pDevice.MV_CC_OpenDevice_NET(MyCamera.MV_ACCESS_Exclusive, 0);
            if (nRet != MyCamera.MV_OK)
            {
                Log($"打开设备失败，错误码：{nRet:X8}");
                m_pDevice.MV_CC_DestroyDevice_NET();
                return;
            }

            m_isConnected = true;
            Log("相机连接成功！");
            UpdateButtonState(true, false);
        }

        // 开始采集按钮
        private void StartGrabButton_Click(object sender, RoutedEventArgs e)
        {
            if (m_pDevice == null || !m_isConnected)
            {
                Log("请先连接相机");
                return;
            }

            // 注册图像回调
            MyCamera.cbOutputdelegate callback = new MyCamera.cbOutputdelegate(ImageCallback);
            int nRet = m_pDevice.MV_CC_RegisterImageCallBack_NET(callback, IntPtr.Zero);
            if (nRet != MyCamera.MV_OK)
            {
                Log($"注册图像回调失败，错误码：{nRet:X8}");
                return;
            }

            // 开始采集
            nRet = m_pDevice.MV_CC_StartGrabbing_NET();
            if (nRet != MyCamera.MV_OK)
            {
                Log($"开始采集失败，错误码：{nRet:X8}");
                return;
            }

            m_isGrabbing = true;
            m_nFrameCount = 0;
            Log("开始采集成功");
            UpdateButtonState(true, true);
        }

        // 停止采集按钮
        private void StopGrabButton_Click(object sender, RoutedEventArgs e)
        {
            if (m_pDevice == null)
            {
                return;
            }

            // 停止采集
            if (m_isGrabbing)
            {
                m_pDevice.MV_CC_StopGrabbing_NET();
                m_isGrabbing = false;
                Log("停止采集成功");
            }

            // 关闭设备
            if (m_isConnected)
            {
                m_pDevice.MV_CC_CloseDevice_NET();
                m_isConnected = false;
                Log("相机已断开");
            }

            UpdateButtonState(false, false);
        }

        // 图像回调
        private void ImageCallback(IntPtr pData, ref MyCamera.MV_FRAME_OUT_INFO pFrameInfo, IntPtr pUser)
        {
            // 更新帧计数
            m_nFrameCount++;
            Dispatcher.Invoke(() =>
            {
                FrameCountText.Text = $"帧数: {m_nFrameCount}";
            });

            // 获取图像尺寸
            int nWidth = (int)pFrameInfo.nWidth;
            int nHeight = (int)pFrameInfo.nHeight;
            int nDataLen = (int)pFrameInfo.nFrameLen;

            if (nWidth <= 0 || nHeight <= 0 || nDataLen <= 0)
            {
                return;
            }

            // 分配图像缓冲区
            byte[] pImageBuffer = new byte[nDataLen];
            Marshal.Copy(pData, pImageBuffer, 0, nDataLen);

            // 显示图像
            Dispatcher.Invoke(() =>
            {
                try
                {
                    BitmapSource bitmap = DisplayImage(pImageBuffer, nWidth, nHeight, pFrameInfo.enPixelType);
                    CameraDisplay.Source = bitmap;
                }
                catch (Exception ex)
                {
                    // 忽略显示错误
                }
            });
        }

        // 显示图像
        private BitmapSource DisplayImage(byte[] pData, int nWidth, int nHeight, MyCamera.MvGvspPixelType nPixelType)
        {
            // 根据像素格式创建不同的图像
            int stride = nWidth * 3;
            byte[] rgbData = new byte[nHeight * stride];

            // 如果是彩色图像，直接转换
            if (nPixelType == MyCamera.MvGvspPixelType.PixelType_Gvsp_RGB8_Packed ||
                nPixelType == MyCamera.MvGvspPixelType.PixelType_Gvsp_BGR8_Packed)
            {
                // 假设是RGB8_Packed
                for (int i = 0; i < pData.Length && i < rgbData.Length; i++)
                {
                    rgbData[i] = pData[i];
                }
            }
            else
            {
                // 对于其他格式，简单复制数据
                Array.Copy(pData, rgbData, Math.Min(pData.Length, rgbData.Length));
            }

            // 创建BitmapSource
            BitmapSource bitmap = BitmapSource.Create(
                nWidth, nHeight,
                96, 96,
                System.Windows.Media.PixelFormats.Rgb24,
                null,
                rgbData,
                stride);

            bitmap.Freeze();
            return bitmap;
        }

        // 保存图像按钮
        private void CaptureButton_Click(object sender, RoutedEventArgs e)
        {
            if (CameraDisplay.Source == null)
            {
                Log("没有可保存的图像");
                return;
            }

            lock (m_saveLock)
            {
                try
                {
                    BitmapSource bitmap = CameraDisplay.Source as BitmapSource;
                    if (bitmap != null)
                    {
                        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "625RayPrint");
                        Directory.CreateDirectory(folder);

                        string filename = Path.Combine(folder, $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                        
                        using (var fileStream = new FileStream(filename, FileMode.Create))
                        {
                            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            encoder.Save(fileStream);
                        }

                        Log($"图像已保存：{filename}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"保存图像失败：{ex.Message}");
                }
            }
        }

        // 清空日志按钮
        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            LogTextBox.Clear();
            Log("日志已清空");
        }
    }
}
