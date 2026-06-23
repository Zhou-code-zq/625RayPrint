using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Drawing;
using System.Drawing.Imaging;
using MvCamCtrl.NET;

namespace WpfApp1
{
    public partial class VisionInspectionPage : Page
    {
        // ============ SDK 4.7.0 API ============
        // 参考: https://wenku.csdn.net/answer/6o2jk4xevr
        // API 签名来自 MVS 4.7.0 (shankeda 参考项目同款版本)
        // ============ SDK 4.7.0 API ============

        private MyCamera m_pMyCamera = new MyCamera();
        private bool m_bIsGrabbing = false;
        private IntPtr m_hWnd;
        private System.Threading.Thread m_hDisplayThread = null;
        private bool m_bExitDisplayThread = false;
        private int m_nFrameCount = 0;
        private object m_FrameCountLock = new object();

        // 预分配缓存（避免每帧分配）
        private byte[] m_pBufForSaveImage = null;
        private uint m_nBufSizeForSaveImage = 0;

        // 预分配转换缓存
        private byte[] m_pConvertBuf = null;
        private uint m_nConvertBufSize = 0;

        // 图片保存互斥锁
        private object m_SaveImageLock = new object();

        // 回调委托（SDK 4.7.0 专用）
        private MyCamera.cbOutputExdelegate m_cbImage;

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
            if (CameraHost.Child is System.Windows.Forms.PictureBox pic)
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
                        string serial = Encoding.ASCII.GetString(stGigEInfo.chSerialNumber).TrimEnd('\0');
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
                        string serial = Encoding.ASCII.GetString(stUsbInfo.chSerialNumber).TrimEnd('\0');
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
                nRet = m_pMyCamera.MV_CC_CreateDevice_NET(ref stDeviceInfo);
                if (MyCamera.MV_OK != nRet)
                {
                    AppendLog($"[视觉] 创建设备失败，错误码: 0x{nRet:X}");
                    return;
                }

                // 4. 打开设备（SDK 4.7.0: 0 参数）
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

                // 7. 注册图像回调（SDK 4.7.0: cbOutputExdelegate + RegisterImageCallBackEx_NET）
                m_cbImage = new MyCamera.cbOutputExdelegate(OnGetImage);
                nRet = m_pMyCamera.MV_CC_RegisterImageCallBackEx_NET(m_cbImage, IntPtr.Zero);
                if (MyCamera.MV_OK != nRet)
                {
                    AppendLog($"[视觉] 注册图像回调失败，错误码: 0x{nRet:X}");
                }

                // 8. 开始采集（SDK 4.7.0: 0 参数）
                nRet = m_pMyCamera.MV_CC_StartGrabbing_NET();
                if (MyCamera.MV_OK != nRet)
                {
                    AppendLog($"[视觉] 开始采集失败，错误码: 0x{nRet:X}");
                    m_pMyCamera.MV_CC_CloseDevice_NET();
                    m_pMyCamera.MV_CC_DestroyDevice_NET();
                    return;
                }

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

                // 停止采集（SDK 4.7.0: 0 参数）
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

        // 图像回调（SDK 4.7.0: cbOutputExdelegate 签名）
        // IntPtr pData: 图像数据地址
        // ref MV_FRAME_OUT_INFO_EX: 帧信息（nWidth, nHeight, nFrameLen, enPixelType）
        // IntPtr pUser: 用户数据
        private void OnGetImage(IntPtr pData, ref MV_FRAME_OUT_INFO_EX pFrameInfo, IntPtr pUser)
        {
            try
            {
                if (pFrameInfo.nFrameLen == 0 || pFrameInfo.nWidth == 0 || pFrameInfo.nHeight == 0)
                    return;

                // 帧计数
                lock (m_FrameCountLock)
                {
                    m_nFrameCount++;
                    int count = m_nFrameCount;
                    Dispatcher.Invoke(() => { FrameCountText.Text = count.ToString(); });
                }

                // 调用 SDK 显示（SDK 4.7.0: 1 参数 IntPtr）
                if (m_hWnd != IntPtr.Zero)
                {
                    m_pMyCamera.MV_CC_Display_NET(m_hWnd);
                }
            }
            catch { }
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
                    // 获取当前帧
                    MV_FRAME_OUT stFrameOut = new MV_FRAME_OUT();
                    int nRet = m_pMyCamera.MV_CC_GetImageBuffer_NET(ref stFrameOut, 1000);
                    if (nRet != MyCamera.MV_OK)
                    {
                        AppendLog($"[视觉] 取图失败，错误码: 0x{nRet:X}");
                        return;
                    }

                    // 获取帧信息（SDK 4.7.0 用 stFrameOut.stFrameInfo）
                    MV_FRAME_OUT_INFO_EX stFrameInfo = stFrameOut.stFrameInfo;
                    IntPtr pData = stFrameOut.pImageAddr;
                    uint nWidth = stFrameInfo.nWidth;
                    uint nHeight = stFrameInfo.nHeight;
                    uint nFrameLen = stFrameInfo.nFrameLen;
                    MyCamera.MvGvspPixelType enPixelType = stFrameInfo.enPixelType;

                    if (pData == IntPtr.Zero || nFrameLen == 0)
                    {
                        m_pMyCamera.MV_CC_FreeImageBuffer_NET(ref stFrameOut);
                        AppendLog("[视觉] 取图数据无效");
                        return;
                    }

                    // 确定保存路径
                    string saveDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Images");
                    if (!System.IO.Directory.Exists(saveDir))
                        System.IO.Directory.CreateDirectory(saveDir);

                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                    string bmpPath = System.IO.Path.Combine(saveDir, $"img_{timestamp}.bmp");

                    // 预分配缓存
                    uint nSaveBufSize = nFrameLen * 3 + 2048;
                    if (m_pBufForSaveImage == null || m_pBufForSaveImage.Length < nSaveBufSize)
                    {
                        m_pBufForSaveImage = new byte[nSaveBufSize];
                    }

                    // 像素格式转换目标
                    MyCamera.MvGvspPixelType enDstPixelType;
                    System.Drawing.Imaging.PixelFormat fmt;

                    if (IsMonoPixelFormat(enPixelType))
                    {
                        enDstPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8;
                        fmt = System.Drawing.Imaging.PixelFormat.Format8bppIndexed;
                    }
                    else
                    {
                        enDstPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_RGB8_Packed;
                        fmt = System.Drawing.Imaging.PixelFormat.Format24bppRgb;
                    }

                    // 转换像素格式
                    IntPtr pDst = Marshal.UnsafeAddrOfPinnedArrayElement(m_pBufForSaveImage, 0);

                    MyCamera.MV_PIXEL_CONVERT_PARAM stConvertParam = new MyCamera.MV_PIXEL_CONVERT_PARAM
                    {
                        nWidth = nWidth,
                        nHeight = nHeight,
                        pSrcData = pData,
                        nSrcDataLen = nFrameLen,
                        enSrcPixelType = enPixelType,
                        enDstPixelType = enDstPixelType,
                        pDstBuffer = pDst,
                        nDstBufferSize = nSaveBufSize
                    };

                    nRet = m_pMyCamera.MV_CC_ConvertPixelType_NET(ref stConvertParam);

                    // 释放缓存
                    m_pMyCamera.MV_CC_FreeImageBuffer_NET(ref stFrameOut);

                    if (nRet != MyCamera.MV_OK)
                    {
                        AppendLog($"[视觉] 像素转换失败，错误码: 0x{nRet:X}，尝试直接保存");
                        // 直接复制原始数据尝试保存
                        try
                        {
                            byte[] rawData = new byte[nFrameLen];
                            Marshal.Copy(pData, rawData, 0, (int)nFrameLen);
                            using (var fs = new FileStream(bmpPath, FileMode.Create))
                            using (var bw = new BinaryWriter(fs))
                            {
                                // BMP 文件头
                                int rowSize = (int)(nWidth * 3);
                                int bmpSize = 54 + rowSize * (int)nHeight;
                                bw.Write((short)0x4D42);        // BMP 标志
                                bw.Write(bmpSize);             // 文件大小
                                bw.Write(0);                    // 保留
                                bw.Write(54);                   // 数据偏移
                                // DIB 头
                                bw.Write(40);                   // DIB 头大小
                                bw.Write((int)nWidth);         // 宽度
                                bw.Write((int)nHeight);        // 高度
                                bw.Write((short)1);            // 颜色平面
                                bw.Write((short)24);           // 位深
                                bw.Write(0);                    // 压缩方式
                                bw.Write(0);                   // 图像大小
                                bw.Write(0);                   // X 像素/米
                                bw.Write(0);                   // Y 像素/米
                                bw.Write(0);                   // 调色板颜色数
                                bw.Write(0);                   // 重要颜色数
                                // 像素数据（倒置）
                                for (int h = (int)nHeight - 1; h >= 0; h--)
                                {
                                    bw.Write(rawData, h * rowSize, rowSize);
                                }
                            }
                            AppendLog($"[视觉] 原始数据已保存: {bmpPath}");
                        }
                        catch
                        {
                            AppendLog("[视觉] 保存失败");
                        }
                        return;
                    }

                    // 转换为 Bitmap 并保存
                    Bitmap bmp;
                    if (enDstPixelType == MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8)
                    {
                        bmp = new Bitmap((int)nWidth, (int)nHeight, (int)nWidth,
                            System.Drawing.Imaging.PixelFormat.Format8bppIndexed, pDst);
                        ColorPalette cp = bmp.Palette;
                        for (int i = 0; i < 256; i++)
                            cp.Entries[i] = System.Drawing.Color.FromArgb(i, i, i);
                        bmp.Palette = cp;
                    }
                    else
                    {
                        // RGB → BGR 转换
                        int stride = (int)nWidth * 3;
                        for (int h = 0; h < (int)nHeight; h++)
                        {
                            for (int w = 0; w < (int)nWidth; w++)
                            {
                                int idx = h * stride + w * 3;
                                byte tmp = m_pBufForSaveImage[idx];
                                m_pBufForSaveImage[idx] = m_pBufForSaveImage[idx + 2];
                                m_pBufForSaveImage[idx + 2] = tmp;
                            }
                        }
                        bmp = new Bitmap((int)nWidth, (int)nHeight, stride,
                            System.Drawing.Imaging.PixelFormat.Format24bppRgb, pDst);
                    }

                    bmp.Save(bmpPath, System.Drawing.Imaging.ImageFormat.Bmp);
                    bmp.Dispose();

                    AppendLog($"[视觉] 图片已保存: {bmpPath}");
                }
                catch (Exception ex)
                {
                    AppendLog($"[视觉] 保存异常: {ex.Message}");
                }
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
                    return true;
                default:
                    return false;
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
