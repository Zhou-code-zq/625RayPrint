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
        // ============ SDK 4.7.0 回调取图 ============
        // 参考: shankeda 项目 (https://gitee.com/zhouguangya/shankeda)
        // + https://wenku.csdn.net/answer/6o2jk4xevr
        //
        // 回调方式（shankeda 方式）:
        //   委托: cbOutputdelegate
        //   结构: MV_FRAME_OUT_INFO
        //   注册: MV_CC_RegisterImageCallBack_NET
        //
        // 回调流程:
        //   1. StartCamera: 注册回调 -> 开始采集 -> 启动 Display 线程
        //   2. 回调触发: 拷贝原始数据到独立内存 -> 保存指针+宽高+像素格式
        //   3. Display 线程: 从共享内存读取 -> Mono8=调色板Bitmap / 彩色=ConvertPixelType->BGR24 Bitmap
        //   4. Dispatcher 更新 PictureBox.Image
        //
        // 重要: 回调的 pData 指向的内存会在下一帧时被 SDK 覆盖，必须立即拷贝！
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

        // 调试帧计数器
        private int m_nDebugCbFrame = 0;  // 回调帧计数
        private int m_nDebugDispFrame = 0; // 显示帧计数

        private byte[] m_cbRawBuf = null;  // 回调中用于拷贝原始数据的临时缓存

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

        public VisionInspectionPage()
        {
            try
            {
                InitializeComponent();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"[视觉] InitializeComponent失败: {ex.Message}\n{ex.StackTrace}", "构造函数错误",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                throw;
            }
        }

        private void VisionInspectionPage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                AppendLog("[视觉] 页面加载成功");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"[视觉] 页面Loaded异常: {ex.Message}\n{ex.StackTrace}", "运行时错误",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
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
        // 参考 shankeda 项目 + https://wenku.csdn.net/answer/6o2jk4xevr
        // 回调委托: cbOutputdelegate (SDK 4.7.0 shankeda 使用的是这个)
        // 帧结构: MV_FRAME_OUT_INFO (含 nWidth/nHeight/nFrameLen/enPixelType)
        // 注册: MV_CC_RegisterImageCallBack_NET
        private void ImageCallBack_NonEx(IntPtr pData, ref MyCamera.MV_FRAME_OUT_INFO pFrameInfo, IntPtr pUser)
        {
            try
            {
                // 记录帧计数
                lock (m_FrameCountLock)
                {
                    m_nFrameCount++;
                }

                // 保存最新帧信息（加锁保护）
                lock (m_ImageLock)
                {
                    m_nImageWidth = (int)pFrameInfo.nWidth;
                    m_nImageHeight = (int)pFrameInfo.nHeight;
                    m_enPixelType = pFrameInfo.enPixelType;
                    // 重要: 拷贝原始数据！SDK 回调的 pData 指向的内存会在下一帧时被覆盖
                    uint nFrameLen = pFrameInfo.nFrameLen;
                    if (nFrameLen > 0)
                    {
                        if (m_cbRawBuf == null || m_cbRawBuf.Length < nFrameLen)
                            m_cbRawBuf = new byte[nFrameLen];
                        Marshal.Copy(pData, m_cbRawBuf, 0, (int)nFrameLen);
                        // 释放旧内存（如果存在）
                        if (m_pLatestImageData != IntPtr.Zero)
                            Marshal.FreeHGlobal(m_pLatestImageData);
                        // 分配新内存并拷贝数据
                        m_pLatestImageData = Marshal.AllocHGlobal((int)nFrameLen);
                        Marshal.Copy(m_cbRawBuf, 0, m_pLatestImageData, (int)nFrameLen);
                        m_nLatestImageLen = nFrameLen;
                    }
                }

                // 更新帧计数显示（每30帧打印一次日志）
                int frameCount;
                lock (m_FrameCountLock)
                {
                    frameCount = m_nFrameCount;
                }
                if (frameCount == 1)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        AppendLog($"[回调] 收到第1帧! {pFrameInfo.nWidth}x{pFrameInfo.nHeight} pixelType={pFrameInfo.enPixelType}");
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
                else if (frameCount > 0 && frameCount % 30 == 0)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        AppendLog($"[回调] 已收到 {frameCount} 帧...");
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    FrameCountText.Text = frameCount.ToString();
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[回调-NE] 异常: {ex.Message}");
            }
        }

        // Ex 回调（备用，当非 Ex 版本失败时使用）
        private void ImageCallBack_Ex(IntPtr pData, ref MyCamera.MV_FRAME_OUT_INFO_EX pFrameInfo, IntPtr pUser)
        {
            try
            {
                lock (m_FrameCountLock)
                {
                    m_nFrameCount++;
                }
                lock (m_ImageLock)
                {
                    m_nImageWidth = (int)pFrameInfo.nWidth;
                    m_nImageHeight = (int)pFrameInfo.nHeight;
                    m_enPixelType = pFrameInfo.enPixelType;
                    uint nFrameLen = pFrameInfo.nFrameLen;
                    if (nFrameLen > 0)
                    {
                        if (m_cbRawBuf == null || m_cbRawBuf.Length < nFrameLen)
                            m_cbRawBuf = new byte[nFrameLen];
                        Marshal.Copy(pData, m_cbRawBuf, 0, (int)nFrameLen);
                        if (m_pLatestImageData != IntPtr.Zero)
                            Marshal.FreeHGlobal(m_pLatestImageData);
                        m_pLatestImageData = Marshal.AllocHGlobal((int)nFrameLen);
                        Marshal.Copy(m_cbRawBuf, 0, m_pLatestImageData, (int)nFrameLen);
                        m_nLatestImageLen = nFrameLen;
                    }
                }
                lock (m_FrameCountLock)
                {
                    frameCount = m_nFrameCount;
                }
                if (frameCount == 1)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        AppendLog($"[回调-Ex] 收到第1帧! {pFrameInfo.nWidth}x{pFrameInfo.nHeight} pixelType={pFrameInfo.enPixelType}");
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    FrameCountText.Text = frameCount.ToString();
                }), System.Windows.Threading.DispatcherPriority.Background);
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
                AppendLog($"[视觉] 设置TriggerMode=0, nRet=0x{nRet:X}");

                // 6. 获取 PayloadSize 用于预分配缓存
                MyCamera.MVCC_INTVALUE stParam = new MyCamera.MVCC_INTVALUE();
                nRet = m_pMyCamera.MV_CC_GetIntValue_NET("PayloadSize", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    m_nBufSizeForSaveImage = stParam.nCurValue * 3 + 2048;
                    m_pBufForSaveImage = new byte[m_nBufSizeForSaveImage];
                    m_pConvertBuf = new byte[m_nBufSizeForSaveImage];
                    m_nConvertBufSize = m_nBufSizeForSaveImage;
                    AppendLog($"[视觉] PayloadSize={stParam.nCurValue}, 分配缓存={m_nBufSizeForSaveImage}");
                }
                else
                {
                    AppendLog($"[视觉] 获取PayloadSize失败, nRet=0x{nRet:X}, 使用默认值20MB");
                    m_nBufSizeForSaveImage = 20 * 1024 * 1024;
                    m_pBufForSaveImage = new byte[m_nBufSizeForSaveImage];
                    m_pConvertBuf = new byte[m_nBufSizeForSaveImage];
                    m_nConvertBufSize = m_nBufSizeForSaveImage;
                }

                // 7. 尝试注册图像回调（使用 shankeda 方式：cbOutputdelegate + RegisterImageCallBack_NET）
                int cbRegRet = -1;
                try
                {
                    MyCamera.cbOutputdelegate cb = new MyCamera.cbOutputdelegate(ImageCallBack_NonEx);
                    cbRegRet = m_pMyCamera.MV_CC_RegisterImageCallBack_NET(cb, IntPtr.Zero);
                    if (cbRegRet == MyCamera.MV_OK)
                    {
                        AppendLog("[视觉] 回调注册成功 (cbOutputdelegate + RegisterImageCallBack_NET)");
                    }
                    else
                    {
                        AppendLog($"[视觉] 回调注册失败，错误码=0x{cbRegRet:X}，尝试 Ex 版本");
                        // 备用: Ex 版本
                        try
                        {
                            MyCamera.cbOutputExdelegate cbEx = new MyCamera.cbOutputExdelegate(ImageCallBack_Ex);
                            cbRegRet = m_pMyCamera.MV_CC_RegisterImageCallBackEx_NET(cbEx, IntPtr.Zero);
                            if (cbRegRet == MyCamera.MV_OK)
                            {
                                AppendLog("[视觉] 回调注册成功 (cbOutputExdelegate + RegisterImageCallBackEx_NET)");
                            }
                            else
                            {
                                AppendLog($"[视觉] 回调 Ex 注册也失败，错误码=0x{cbRegRet:X}");
                            }
                        }
                        catch (Exception ex2)
                        {
                            AppendLog($"[视觉] 回调 Ex 注册异常: {ex2.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"[视觉] 回调注册异常: {ex.Message}");
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
                AppendLog("[视觉] 开始采集成功");

                // 9. 启动 Display 线程（从回调获取帧，手动渲染到 PictureBox）
                m_bExitDisplayThread = false;
                m_hDisplayThread = new System.Threading.Thread(DisplayThread);
                m_hDisplayThread.IsBackground = true;
                m_hDisplayThread.Start();
                AppendLog("[视觉] Display线程已启动，等待回调...");

                m_bIsGrabbing = true;
                m_nFrameCount = 0;
                AppendLog("[视觉] 采集已开始，等待回调...");

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

                // 释放回调中分配的图像内存
                lock (m_ImageLock)
                {
                    if (m_pLatestImageData != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(m_pLatestImageData);
                        m_pLatestImageData = IntPtr.Zero;
                    }
                    m_nImageWidth = 0;
                    m_nImageHeight = 0;
                    m_nLatestImageLen = 0;
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
            int logInterval = 0;
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
                        bool isMono = IsMonoPixelFormat(enPixelType);
                        Bitmap bitmap = null;

                        if (isMono)
                        {
                            // Mono8 -> 灰度 Bitmap
                            byte[] monoBuf = new byte[nWidth * nHeight];
                            Marshal.Copy(pData, monoBuf, 0, nWidth * nHeight);
                            bitmap = new Bitmap(nWidth, nHeight, PixelFormat.Format8bppIndexed);
                            ColorPalette cp = bitmap.Palette;
                            for (int i = 0; i < 256; i++)
                            {
                                cp.Entries[i] = System.Drawing.Color.FromArgb(i, i, i);
                            }
                            bitmap.Palette = cp;
                            BitmapData bmpData = bitmap.LockBits(
                                new Rectangle(0, 0, nWidth, nHeight),
                                ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
                            Marshal.Copy(monoBuf, 0, bmpData.Scan0, nWidth * nHeight);
                            bitmap.UnlockBits(bmpData);
                        }
                        else
                        {
                            // 彩色: 转换为 BGR8_Packed（Bitmap 期望 BGR 顺序）
                            bitmap = ConvertToBgr24BitmapSafe(pData, nWidth, nHeight, enPixelType, nDataLen);
                        }

                        if (bitmap != null)
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    if (CameraDisplayHost.Child is System.Windows.Forms.PictureBox pic)
                                    {
                                        pic.SizeMode = PictureBoxSizeMode.Zoom;
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
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Display] 渲染异常: {ex.Message}");
                    }
                }

                // 每秒打印一次等待状态日志
                logInterval++;
                if (logInterval >= 33)
                {
                    logInterval = 0;
                    if (pData == IntPtr.Zero)
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            AppendLog("[Display] 等待回调...（如长时间未收到帧，请检查相机连接和触发模式）");
                        }), System.Windows.Threading.DispatcherPriority.Background);
                    }
                }

                System.Threading.Thread.Sleep(30);
            }
        }

        // 将原始图像数据转换为 BGR24 Bitmap（用于彩色相机）
        private Bitmap ConvertToBgr24BitmapSafe(IntPtr pData, int nWidth, int nHeight,
            MyCamera.MvGvspPixelType enSrcPixelType, uint nSrcDataLen)
        {
            try
            {
                // 计算所需缓存大小
                uint neededSize = (uint)(nWidth * nHeight * 3);
                if (m_pConvertBuf == null || m_nConvertBufSize < neededSize)
                {
                    m_nConvertBufSize = neededSize;
                    m_pConvertBuf = new byte[m_nConvertBufSize];
                }

                IntPtr pDst = Marshal.UnsafeAddrOfPinnedArrayElement(m_pConvertBuf, 0);

                MyCamera.MV_PIXEL_CONVERT_PARAM stConvertParam = new MyCamera.MV_PIXEL_CONVERT_PARAM
                {
                    nWidth = (ushort)nWidth,
                    nHeight = (ushort)nHeight,
                    pSrcData = pData,
                    nSrcDataLen = nSrcDataLen,
                    enSrcPixelType = enSrcPixelType,
                    // Bitmap 是 BGR 顺序，直接转 BGR8_Packed
                    enDstPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_BGR8_Packed,
                    pDstBuffer = pDst,
                    nDstBufferSize = m_nConvertBufSize
                };

                int nRet = m_pMyCamera.MV_CC_ConvertPixelType_NET(ref stConvertParam);
                if (nRet != MyCamera.MV_OK)
                {
                    System.Diagnostics.Debug.WriteLine($"[Display] 像素转换失败 nRet=0x{nRet:X}, pixelType={enSrcPixelType}");
                    return null;
                }

                // 创建 Bitmap（BGR 顺序 -> Format24bppRgb 正好匹配）
                Bitmap bitmap = new Bitmap(nWidth, nHeight, PixelFormat.Format24bppRgb);
                BitmapData bmpData = bitmap.LockBits(
                    new Rectangle(0, 0, nWidth, nHeight),
                    ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                Marshal.Copy(m_pConvertBuf, 0, bmpData.Scan0, nWidth * nHeight * 3);
                bitmap.UnlockBits(bmpData);
                return bitmap;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Display] 像素转换异常: {ex.Message}");
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
