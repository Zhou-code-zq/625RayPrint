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
        // MV_CC_Display_NET: 1参数(IntPtr)
        // MV_CC_GetOneFrameTimeout_NET: byte[]方式
        // ============ SDK 4.7.0 API ============

        private MyCamera m_pMyCamera = new MyCamera();
        private bool m_bIsGrabbing = false;
        private IntPtr m_hWnd;
        private System.Threading.Thread m_hDisplayThread = null;
        private bool m_bExitDisplayThread = false;
        private int m_nFrameCount = 0;
        private object m_FrameCountLock = new object();

        // 预分配缓存
        private byte[] m_pBufForSaveImage = null;
        private byte[] m_pConvertBuf = null;
        private uint m_nBufSizeForSaveImage = 0;
        private uint m_nConvertBufSize = 0;

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
            // 获取 WindowsFormsHost 的句柄用于 Display_NET
            if (CameraDisplayHost.Child is System.Windows.Forms.PictureBox pic)
            {
                m_hWnd = pic.Handle;
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

                // 创建设备对象（SDK 4.7.0: 静态方法，pDeviceInfo 从 Marshal.ReadIntPtr 遍历）
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

                // 7. 开始采集（SDK 4.7.0: 0参数）
                nRet = m_pMyCamera.MV_CC_StartGrabbing_NET();
                if (MyCamera.MV_OK != nRet)
                {
                    AppendLog($"[视觉] 开始采集失败，错误码: 0x{nRet:X}");
                    m_pMyCamera.MV_CC_CloseDevice_NET();
                    m_pMyCamera.MV_CC_DestroyDevice_NET();
                    return;
                }

                // 8. 启动 Display 线程（SDK 4.7.0: Display_NET 1参数 IntPtr）
                m_bExitDisplayThread = false;
                m_hDisplayThread = new System.Threading.Thread(DisplayThread);
                m_hDisplayThread.IsBackground = true;
                m_hDisplayThread.Start();

                m_bIsGrabbing = true;
                m_nFrameCount = 0;
                AppendLog("[视觉] 采集已开始");

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

                // 停止采集线程
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
                });
            }
            catch (Exception ex)
            {
                AppendLog($"[视觉] 停止异常: {ex.Message}");
            }
        }

        // Display 线程：持续调用 Display_NET 渲染画面
        private void DisplayThread()
        {
            while (!m_bExitDisplayThread)
            {
                if (m_bIsGrabbing && m_hWnd != IntPtr.Zero)
                {
                    try
                    {
                        // SDK 4.7.0: Display_NET 1参数 (IntPtr hWnd)
                        m_pMyCamera.MV_CC_Display_NET(m_hWnd);
                    }
                    catch { }
                }
                System.Threading.Thread.Sleep(30); // ~33fps
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
                    if (m_pBufForSaveImage == null || m_pBufForSaveImage.Length < nBufSize)
                    {
                        m_pBufForSaveImage = new byte[nBufSize];
                    }

                    GCHandle handle = GCHandle.Alloc(m_pBufForSaveImage, GCHandleType.Pinned);
                    IntPtr pBuf = handle.AddrOfPinnedObject();
                    MyCamera.MV_FRAME_OUT_INFO_EX stFrameInfo = new MyCamera.MV_FRAME_OUT_INFO_EX();
                    int nRet = m_pMyCamera.MV_CC_GetOneFrameTimeout_NET(pBuf, nBufSize, ref stFrameInfo, 1000);
                    if (nRet != MyCamera.MV_OK)
                    {
                        handle.Free();
                        AppendLog($"[视觉] 取图失败，错误码: 0x{nRet:X}");
                        return;
                    }

                    int nWidth = (int)stFrameInfo.nWidth;
                    int nHeight = (int)stFrameInfo.nHeight;
                    uint nDataLen = stFrameInfo.nFrameLen;
                    MyCamera.MvGvspPixelType enPixelType = stFrameInfo.enPixelType;

                    // 确定保存路径
                    string saveDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Images");
                    if (!System.IO.Directory.Exists(saveDir))
                        System.IO.Directory.CreateDirectory(saveDir);

                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                    string bmpPath = System.IO.Path.Combine(saveDir, $"img_{timestamp}.bmp");

                    // 直接写入 BMP 文件（无格式转换兜底方案）
                    // Mono8: stride = nWidth（每行字节数）
                    WriteBitmapRaw(m_pBufForSaveImage, nWidth, nHeight, nWidth, bmpPath);
                    handle.Free();
                    AppendLog($"[视觉] 图片已保存: {bmpPath}");

                    // 更新帧计数
                    lock (m_FrameCountLock)
                    {
                        m_nFrameCount++;
                        Dispatcher.Invoke(() => { FrameCountText.Text = m_nFrameCount.ToString(); });
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"[视觉] 保存异常: {ex.Message}");
                }
            }
        }

        // BMP 格式转换辅助
        private MyCamera.MvGvspPixelType GetDstPixelType(MyCamera.MvGvspPixelType src)
        {
            switch (src)
            {
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono10:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono10_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono12:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono12_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono16:
                    return MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8;
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BGR8_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_RGB8_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BGRA8_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_RGBA8_Packed:
                    return MyCamera.MvGvspPixelType.PixelType_Gvsp_BGR8_Packed;
                default:
                    return MyCamera.MvGvspPixelType.PixelType_Gvsp_BGR8_Packed;
            }
        }

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

        // 直接写 BMP 文件（不依赖 SDK 转换）
        private void WriteBitmapRaw(byte[] pData, int nWidth, int nHeight, int nDataLen, string bmpPath)
        {
            try
            {
                // 尝试获取像素格式
                MyCamera.MVCC_ENUMVALUE stPixelType = new MyCamera.MVCC_ENUMVALUE();
                m_pMyCamera.MV_CC_GetEnumValue_NET("PixelFormat", ref stPixelType);
                MyCamera.MvGvspPixelType enPixelType = (MyCamera.MvGvspPixelType)stPixelType.nCurValue;

                MyCamera.MvGvspPixelType enDstPixelType = GetDstPixelType(enPixelType);
                bool isMono = IsMonoPixelFormat(enPixelType);
                bool isBGR = (enDstPixelType == MyCamera.MvGvspPixelType.PixelType_Gvsp_BGR8_Packed);

                // 字节数/像素
                int bytesPerPixel = isMono ? 1 : 3;
                int stride = ((nWidth * bytesPerPixel + 3) / 4) * 4;
                int bmpSize = 54 + stride * nHeight;
                byte[] bmpData = new byte[bmpSize];

                // BMP 文件头
                bmpData[0] = 0x42; bmpData[1] = 0x4D;                     // "BM"
                BitConverter.GetBytes(bmpSize).CopyTo(bmpData, 2);
                BitConverter.GetBytes(54).CopyTo(bmpData, 10);             // 数据偏移
                // DIB 头
                BitConverter.GetBytes(40).CopyTo(bmpData, 14);             // DIB 头大小
                BitConverter.GetBytes(nWidth).CopyTo(bmpData, 18);          // 宽度
                BitConverter.GetBytes(nHeight).CopyTo(bmpData, 22);          // 高度
                bmpData[26] = 1; bmpData[27] = 0;                          // 颜色平面
                bmpData[28] = (byte)(bytesPerPixel * 8); bmpData[29] = 0;   // 位深
                BitConverter.GetBytes(stride * nHeight).CopyTo(bmpData, 34); // 图像大小

                // 复制像素数据（上下翻转 BMP 格式）
                int copyLen = Math.Min(nDataLen, stride * nHeight);
                for (int h = 0; h < nHeight; h++)
                {
                    int srcRow = h * stride;
                    int dstRow = (nHeight - 1 - h) * stride;
                    Array.Copy(pData, srcRow, bmpData, 54 + dstRow, Math.Min(stride, copyLen - srcRow));
                }

                // Mono8 需要调色板
                if (isMono)
                {
                    // 54-54+1024 = 灰度调色板
                    for (int i = 0; i < 256; i++)
                    {
                        int idx = 54 + i * 4;
                        bmpData[idx] = (byte)i;       // B
                        bmpData[idx + 1] = (byte)i;   // G
                        bmpData[idx + 2] = (byte)i;   // R
                        bmpData[idx + 3] = 0;         // 保留
                    }
                }
                else if (isBGR)
                {
                    // BGR→RGB（Windows BMP 是 BGR 顺序，这里直接写就行不需要转换）
                    // 因为相机 BGR8_Packed 本身就是 BGR 顺序，与 BMP 一致
                }

                File.WriteAllBytes(bmpPath, bmpData);
            }
            catch (Exception ex)
            {
                AppendLog($"[视觉] BMP写入异常: {ex.Message}");
            }
        }

        private void AppendLog(string msg)
        {
            string log = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText(log + Environment.NewLine);
                LogTextBox.ScrollToEnd();
            });
        }
    }
}
