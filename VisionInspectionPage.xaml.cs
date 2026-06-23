using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Drawing;
using System.Drawing.Imaging;
using MvCamCtrl.NET;

namespace WpfApp1
{
    public partial class VisionInspectionPage : Page
    {
        // ============ SDK 4.7.0 API ============
        // 参考: https://wenku.csdn.net/answer/6o2jk4xevr (MVS 4.7.0 回调示例)
        // MV_CC_OpenDevice_NET: 0参数
        // MV_CC_StartGrabbing_NET: 0参数
        // MV_CC_RegisterImageCallBackEx_NET: 注册帧回调（使用 cbOutputExdelegate）
        // MV_CC_Display_ONE_FRAME_NET: 显示单帧
        // ============ SDK 4.7.0 API ============

        private MyCamera m_pMyCamera = new MyCamera();
        private bool m_bIsGrabbing = false;
        private System.Threading.Thread m_hDisplayThread = null;
        private bool m_bExitDisplayThread = false;
        private int m_nFrameCount = 0;
        private object m_FrameCountLock = new object();

        // 预分配缓存
        private byte[] m_pBufForSaveImage = null;
        private byte[] m_pConvertBuf = null;
        private uint m_nBufSizeForSaveImage = 0;
        private uint m_nConvertBufSize = 0;

        // 回调帧信息（用于 SaveImage 和 Display）
        private int m_nImageWidth = 0;
        private int m_nImageHeight = 0;
        private MyCamera.MvGvspPixelType m_enPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Undefined;
        private IntPtr m_pLatestImageData = IntPtr.Zero;
        private uint m_nLatestImageLen = 0;
        private object m_ImageLock = new object();

        // 图片保存互斥锁
        private object m_SaveImageLock = new object();

        // 相机配置参数
        private static string s_strCameraSerial = "";
        private static uint s_nDeviceType = 0;  // MV_GIGE_DEVICE 或 MV_USB_DEVICE

        public static void SetCameraConfig(string serial, uint deviceType)
        {
            s_strCameraSerial = serial;
            s_nDeviceType = deviceType;
        }

        private void VisionInspectionPage_Loaded(object sender, RoutedEventArgs e)
        {
            AppendLog("[视觉] 页面加载");
        }

        private void VisionInspectionPage_Unloaded(object sender, RoutedEventArgs e)
        {
            StopCamera();
        }

        private void StartCameraButton_Click(object sender, RoutedEventArgs e)
        {
            StartCamera();
        }

        private void StopCameraButton_Click(object sender, RoutedEventArgs e)
        {
            StopCamera();
        }

        private void SaveImageButton_Click(object sender, RoutedEventArgs e)
        {
            SaveImage();
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            LogTextBox.Clear();
        }

        // ============ SDK 4.7.0 回调取图 ============
        // 参考: https://wenku.csdn.net/answer/6o2jk4xevr
        // cbOutputExdelegate(IntPtr pData, ref MV_FRAME_OUT_INFO_EX pFrameInfo, IntPtr pUser)
        // 调用 MV_CC_RegisterImageCallBackEx_NET 注册
        private void ImageCallBack(IntPtr pData, ref MyCamera.MV_FRAME_OUT_INFO_EX pFrameInfo, IntPtr pUser)
        {
            try
            {
                // 记录帧计数
                lock (m_FrameCountLock)
                {
                    m_nFrameCount++;
                }

                // 保存最新帧信息（用于 Display 和 SaveImage）
                lock (m_ImageLock)
                {
                    m_nImageWidth = (int)pFrameInfo.nWidth;
                    m_nImageHeight = (int)pFrameInfo.nHeight;
                    m_enPixelType = pFrameInfo.enPixelType;
                    m_pLatestImageData = pData;
                    m_nLatestImageLen = pFrameInfo.nFrameLen;
                }

                // 更新帧计数显示
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    FrameCountText.Text = m_nFrameCount.ToString();
                }));
            }
            catch { }
        }

        private void StartCamera()
        {
            try
            {
                AppendLog("[视觉] 开始初始化相机...");

                // 1. 枚举设备
                MyCamera.MV_CC_DEVICE_INFO_LIST stDeviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
                uint nTLayerType = MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE;
                int nRet = MyCamera.MV_CC_EnumDevices_NET(nTLayerType, ref stDeviceList);
                if (MyCamera.MV_OK != nRet)
                {
                    AppendLog($"[视觉] 枚举设备失败，错误码: 0x{nRet:X}");
                    return;
                }

                if (stDeviceList.nDeviceNum == 0)
                {
                    AppendLog("[视觉] 未找到任何设备！");
                    return;
                }

                AppendLog($"[视觉] 发现 {stDeviceList.nDeviceNum} 个设备");

                // 2. 按序列号匹配设备
                IntPtr pDeviceInfo = IntPtr.Zero;
                bool bFound = false;

                for (int i = 0; i < stDeviceList.nDeviceNum; i++)
                {
                    pDeviceInfo = Marshal.ReadIntPtr(stDeviceList.pDeviceInfo, i * IntPtr.Size);

                    MyCamera.MV_CC_DEVICE_INFO stDeviceInfo = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo, typeof(MyCamera.MV_CC_DEVICE_INFO));

                    if (stDeviceInfo.nTLayerType == MyCamera.MV_GIGE_DEVICE)
                    {
                        IntPtr pGigEInfo = Marshal.ReadIntPtr(pDeviceInfo, 0);
                        MyCamera.MV_GIGE_DEVICE_INFO stGigEInfo = (MyCamera.MV_GIGE_DEVICE_INFO)Marshal.PtrToStructure(pGigEInfo, typeof(MyCamera.MV_GIGE_DEVICE_INFO));
                        string serial = stGigEInfo.chSerialNumber.TrimEnd('\0');
                        AppendLog($"[视觉] GigE 设备 {i}: 序列号={serial}");
                        if (!string.IsNullOrEmpty(s_strCameraSerial) && serial == s_strCameraSerial)
                        {
                            bFound = true;
                            break;
                        }
                    }
                    else if (stDeviceInfo.nTLayerType == MyCamera.MV_USB_DEVICE)
                    {
                        IntPtr pUsbInfo = Marshal.ReadIntPtr(pDeviceInfo, 0);
                        MyCamera.MV_USB3_DEVICE_INFO stUsbInfo = (MyCamera.MV_USB3_DEVICE_INFO)Marshal.PtrToStructure(pUsbInfo, typeof(MyCamera.MV_USB3_DEVICE_INFO));
                        string serial = stUsbInfo.chSerialNumber.TrimEnd('\0');
                        AppendLog($"[视觉] USB 设备 {i}: 序列号={serial}");
                        if (!string.IsNullOrEmpty(s_strCameraSerial) && serial == s_strCameraSerial)
                        {
                            bFound = true;
                            break;
                        }
                    }
                }

                if (!bFound)
                {
                    AppendLog($"[视觉] 未找到序列号为 {s_strCameraSerial} 的设备");
                    return;
                }

                // 3. 创建设备对象
                MyCamera.MV_CC_DEVICE_INFO stDeviceForCreate = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo, typeof(MyCamera.MV_CC_DEVICE_INFO));
                nRet = m_pMyCamera.MV_CC_CreateDevice_NET(ref stDeviceForCreate);
                if (MyCamera.MV_OK != nRet)
                {
                    AppendLog($"[视觉] 创建设备失败，错误码: 0x{nRet:X}");
                    return;
                }

                // 4. 打开设备（SDK 4.7.0: 0参数）
                nRet = m_pMyCamera.MV_CC_OpenDevice_NET();
                if (MyCamera.MV_OK != nRet)
                {
                    AppendLog($"[视觉] 打开设备失败，错误码: 0x{nRet:X}");
                    m_pMyCamera.MV_CC_DestroyDevice_NET();
                    return;
                }

                AppendLog("[视觉] 设备打开成功");

                // 5. 设置触发模式为连续采集
                nRet = m_pMyCamera.MV_CC_SetEnumValue_NET("TriggerMode", 0);
                if (MyCamera.MV_OK != nRet)
                {
                    AppendLog($"[视觉] 设置触发模式失败，错误码: 0x{nRet:X}");
                }

                // 6. 获取 PayloadSize 用于预分配缓存
                MyCamera.MVCC_INTVALUE stParam = new MyCamera.MVCC_INTVALUE();
                nRet = m_pMyCamera.MV_CC_GetIntValue_NET("PayloadSize", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    m_nBufSizeForSaveImage = stParam.nCurValue * 3 + 2048;
                    m_pBufForSaveImage = new byte[m_nBufSizeForSaveImage];
                    m_pConvertBuf = new byte[m_nBufSizeForSaveImage];
                    m_nConvertBufSize = m_nBufSizeForSaveImage;
                    AppendLog($"[视觉] PayloadSize={stParam.nCurValue}，已分配缓存");
                }

                // 7. 注册图像回调（SDK 4.7.0: cbOutputExdelegate）
                // 参考: https://wenku.csdn.net/answer/6o2jk4xevr
                MyCamera.cbOutputExdelegate cb = new MyCamera.cbOutputExdelegate(ImageCallBack);
                nRet = m_pMyCamera.MV_CC_RegisterImageCallBackEx_NET(cb, IntPtr.Zero);
                if (MyCamera.MV_OK != nRet)
                {
                    AppendLog($"[视觉] 注册回调失败，错误码: 0x{nRet:X}");
                }

                // 8. 开始采集（SDK 4.7.0: 0参数）
                nRet = m_pMyCamera.MV_CC_StartGrabbing_NET();
                if (MyCamera.MV_OK != nRet)
                {
                    AppendLog($"[视觉] 开始采集失败，错误码: 0x{nRet:X}");
                    m_pMyCamera.MV_CC_CloseDevice_NET();
                    m_pMyCamera.MV_CC_DestroyDevice_NET();
                    return;
                }

                // 9. 启动 Display 线程（从回调获取帧，手动渲染到 PictureBox）
                m_bExitDisplayThread = false;
                m_hDisplayThread = new System.Threading.Thread(DisplayThread);
                m_hDisplayThread.IsBackground = true;
                m_hDisplayThread.Start();

                m_bIsGrabbing = true;
                m_nFrameCount = 0;
                AppendLog("[视觉] 采集已开始，回调注册成功");

                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = "运行中";
                    StartCameraButton.IsEnabled = false;
                    StopCameraButton.IsEnabled = true;
                });
            }
            catch (Exception ex)
            {
                AppendLog($"[视觉] 启动异常: {ex.Message}");
            }
        }

        private void StopCamera()
        {
            try
            {
                AppendLog("[视觉] 正在停止...");

                // 停止 Display 线程
                m_bExitDisplayThread = true;
                if (m_hDisplayThread != null && m_hDisplayThread.IsAlive)
                {
                    m_hDisplayThread.Join(1000);
                    m_hDisplayThread = null;
                }

                // 停止采集（SDK 4.7.0: 0参数）
                if (m_bIsGrabbing)
                {
                    m_pMyCamera.MV_CC_StopGrabbing_NET();
                    m_bIsGrabbing = false;
                }

                // 关闭设备
                m_pMyCamera.MV_CC_CloseDevice_NET();

                // 销毁设备对象
                m_pMyCamera.MV_CC_DestroyDevice_NET();

                AppendLog("[视觉] 已停止");

                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = "已停止";
                    StartCameraButton.IsEnabled = true;
                    StopCameraButton.IsEnabled = false;
                    FrameCountText.Text = "0";
                    // 清空显示
                    if (CameraDisplayHost.Child is System.Windows.Forms.PictureBox pic)
                    {
                        pic.Image = null;
                    }
                });
            }
            catch (Exception ex)
            {
                AppendLog($"[视觉] 停止异常: {ex.Message}");
            }
        }

        // Display 线程：从回调获取最新帧，手动渲染到 PictureBox
        private void DisplayThread()
        {
            while (!m_bExitDisplayThread)
            {
                if (!m_bIsGrabbing)
                {
                    System.Threading.Thread.Sleep(30);
                    continue;
                }

                int nWidth = 0, nHeight = 0;
                MyCamera.MvGvspPixelType enPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Undefined;
                IntPtr pData = IntPtr.Zero;
                uint nDataLen = 0;

                // 复制最新帧信息
                lock (m_ImageLock)
                {
                    if (m_pLatestImageData != IntPtr.Zero && m_nImageWidth > 0 && m_nImageHeight > 0)
                    {
                        nWidth = m_nImageWidth;
                        nHeight = m_nImageHeight;
                        enPixelType = m_enPixelType;
                        pData = m_pLatestImageData;
                        nDataLen = m_nLatestImageLen;
                    }
                }

                if (pData != IntPtr.Zero && nWidth > 0 && nHeight > 0)
                {
                    try
                    {
                        Bitmap bitmap = null;
                        bool isMono = IsMonoPixelFormat(enPixelType);

                        if (isMono)
                        {
                            // Mono8 -> 灰度 Bitmap
                            bitmap = new Bitmap(nWidth, nHeight, nWidth, PixelFormat.Format8bppIndexed, pData);
                            ColorPalette cp = bitmap.Palette;
                            for (int i = 0; i < 256; i++)
                            {
                                cp.Entries[i] = System.Drawing.Color.FromArgb(i, i, i);
                            }
                            bitmap.Palette = cp;
                        }
                        else
                        {
                            // RGB/BGR -> 彩色 Bitmap（需要转换）
                            bitmap = ConvertToRgb24Bitmap(pData, nWidth, nHeight);
                        }

                        if (bitmap != null)
                        {
                            // 更新 PictureBox
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    if (CameraDisplayHost.Child is System.Windows.Forms.PictureBox pic)
                                    {
                                        // 缩放模式
                                        pic.SizeMode = PictureBoxSizeMode.Zoom;
                                        // 直接赋值 Image，PictureBox 会自动绘制
                                        // 先 dispose 旧图
                                        if (pic.Image != null)
                                        {
                                            pic.Image.Dispose();
                                        }
                                        pic.Image = bitmap;
                                    }
                                }
                                catch { }
                            }), System.Windows.Threading.DispatcherPriority.Background);
                        }
                    }
                    catch { }
                }

                System.Threading.Thread.Sleep(30); // ~33fps
            }
        }

        // 转换原始图像数据为 RGB24 Bitmap
        private Bitmap ConvertToRgb24Bitmap(IntPtr pData, int nWidth, int nHeight)
        {
            try
            {
                if (m_pConvertBuf == null || m_nConvertBufSize < (uint)(nWidth * nHeight * 3))
                {
                    m_nConvertBufSize = (uint)(nWidth * nHeight * 3);
                    m_pConvertBuf = new byte[m_nConvertBufSize];
                }

                IntPtr pDst = Marshal.UnsafeAddrOfPinnedArrayElement(m_pConvertBuf, 0);

                MyCamera.MV_PIXEL_CONVERT_PARAM stConvertParam = new MyCamera.MV_PIXEL_CONVERT_PARAM
                {
                    nWidth = (ushort)nWidth,
                    nHeight = (ushort)nHeight,
                    pSrcData = pData,
                    nSrcDataLen = m_nLatestImageLen,
                    enSrcPixelType = m_enPixelType,
                    enDstPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_RGB8_Packed,
                    pDstBuffer = pDst,
                    nDstBufferSize = m_nConvertBufSize
                };

                int nRet = m_pMyCamera.MV_CC_ConvertPixelType_NET(ref stConvertParam);
                if (nRet != MyCamera.MV_OK)
                {
                    return null;
                }

                // BGR -> RGB 交换
                for (int i = 0; i < nWidth * nHeight; i++)
                {
                    byte temp = m_pConvertBuf[i * 3];
                    m_pConvertBuf[i * 3] = m_pConvertBuf[i * 3 + 2];
                    m_pConvertBuf[i * 3 + 2] = temp;
                }

                Bitmap bitmap = new Bitmap(nWidth, nHeight, nWidth * 3, PixelFormat.Format24bppRgb, pDst);
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        // 判断是否为灰度格式
        private bool IsMonoPixelFormat(MyCamera.MvGvspPixelType enType)
        {
            switch (enType)
            {
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono10:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono10_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono12:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono12_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono16:
                    return true;
                default:
                    return false;
            }
        }

        private void SaveImage()
        {
            if (!m_bIsGrabbing)
            {
                AppendLog("[视觉] 请先启动相机");
                return;
            }

            lock (m_SaveImageLock)
            {
                try
                {
                    // 使用 GetOneFrameTimeout(IntPtr, uint, ref MV_FRAME_OUT_INFO_EX, int) 获取图像
                    // 20MB 足够应对大多数相机（5000x3000 @ 8bit = 15MB）
                    uint nBufSize = 20 * 1024 * 1024;
                    if (m_pBufForSaveImage == null)
                    {
                        m_pBufForSaveImage = new byte[nBufSize];
                    }

                    GCHandle handle = GCHandle.Alloc(m_pBufForSaveImage, GCHandleType.Pinned);
                    IntPtr pBuf = handle.AddrOfPinnedObject();

                    MyCamera.MV_FRAME_OUT_INFO_EX stFrameInfo = new MyCamera.MV_FRAME_OUT_INFO_EX();
                    int nRet = m_pMyCamera.MV_CC_GetOneFrameTimeout_NET(pBuf, nBufSize, ref stFrameInfo, 1000);

                    if (nRet == MyCamera.MV_OK)
                    {
                        int nWidth = (int)stFrameInfo.nWidth;
                        int nHeight = (int)stFrameInfo.nHeight;
                        uint nFrameLen = stFrameInfo.nFrameLen;
                        MyCamera.MvGvspPixelType enPixelType = stFrameInfo.enPixelType;

                        // 保存目录
                        string saveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "625_Camera");
                        if (!Directory.Exists(saveDir))
                        {
                            Directory.CreateDirectory(saveDir);
                        }

                        // 文件名: 时间戳
                        string timeStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                        string filePath = Path.Combine(saveDir, $"IMG_{timeStamp}.bmp");

                        bool isMono = IsMonoPixelFormat(enPixelType);

                        // 转换为 BMP
                        Bitmap bitmap = null;
                        if (isMono)
                        {
                            // Mono8 -> 灰度 BMP
                            bitmap = new Bitmap(nWidth, nHeight, nWidth, PixelFormat.Format8bppIndexed, pBuf);
                            ColorPalette cp = bitmap.Palette;
                            for (int i = 0; i < 256; i++)
                            {
                                cp.Entries[i] = System.Drawing.Color.FromArgb(i, i, i);
                            }
                            bitmap.Palette = cp;
                        }
                        else
                        {
                            // 彩色 -> RGB24 BMP
                            bitmap = ConvertToRgb24BitmapFromBytes(pBuf, nWidth, nHeight, nFrameLen, enPixelType);
                        }

                        if (bitmap != null)
                        {
                            bitmap.Save(filePath, ImageFormat.Bmp);
                            bitmap.Dispose();
                            AppendLog($"[视觉] 已保存: {filePath}");
                        }
                        else
                        {
                            AppendLog("[视觉] 保存失败: 图像转换错误");
                        }
                    }
                    else
                    {
                        AppendLog($"[视觉] 取帧失败，错误码: 0x{nRet:X}");
                    }

                    handle.Free();
                }
                catch (Exception ex)
                {
                    AppendLog($"[视觉] 保存异常: {ex.Message}");
                }
            }
        }

        // 从 byte[] 缓存转换 RGB24 BMP（用于 SaveImage）
        private Bitmap ConvertToRgb24BitmapFromBytes(IntPtr pBuf, int nWidth, int nHeight, uint nFrameLen, MyCamera.MvGvspPixelType enPixelType)
        {
            try
            {
                // 直接拷贝数据到新数组
                byte[] rgbData = new byte[nWidth * nHeight * 3];
                Marshal.Copy(pBuf, rgbData, 0, rgbData.Length);

                // BGR -> RGB
                for (int i = 0; i < nWidth * nHeight; i++)
                {
                    byte temp = rgbData[i * 3];
                    rgbData[i * 3] = rgbData[i * 3 + 2];
                    rgbData[i * 3 + 2] = temp;
                }

                // 创建 Bitmap
                Bitmap bitmap = new Bitmap(nWidth, nHeight, nWidth * 3, PixelFormat.Format24bppRgb,
                    Marshal.UnsafeAddrOfPinnedArrayElement(rgbData, 0));
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private void AppendLog(string message)
        {
            string logEntry = $"[{DateTime.Now:HH:mm:ss}] {message}";
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LogTextBox.AppendText(logEntry + Environment.NewLine);
                LogTextBox.ScrollToEnd();
            }));
        }
    }
}
