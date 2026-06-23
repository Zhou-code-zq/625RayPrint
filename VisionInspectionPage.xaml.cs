using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MvCamCtrl.NET;

namespace WpfApp1
{
    public partial class VisionInspectionPage : Page
    {
        private MyCamera m_pCSI;
        private string _targetSerial = "";
        private uint _targetDeviceType = MyCamera.MV_GIGE_DEVICE;
        private bool _isGrabbing = false;
        private int _frameCount = 0;
        private Thread _displayThread;
        private bool _displayRunning = false;
        private IntPtr _displayHandle = IntPtr.Zero;

        // GetImageBuffer 用的结构体（对照 CSDN MVS 5.0.1 示例）
        private MyCamera.MV_FRAME_OUT _stImageOut = new MyCamera.MV_FRAME_OUT();

        // 预分配转换缓存（对照 CSDN MVS 5.0.1 示例）
        private byte[] _convertBuffer;
        private uint _convertBufferSize = 0;

        public VisionInspectionPage()
        {
            InitializeComponent();
        }

        public void SetCameraConfig(string serialNo, uint sdkDeviceType)
        {
            _targetSerial = serialNo;
            _targetDeviceType = sdkDeviceType;
            Log(string.Format("相机配置: 序列号={0}, 设备类型={1}", serialNo, sdkDeviceType));
        }

        private void VisionInspectionPage_Loaded(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "就绪";
        }

        private void VisionInspectionPage_Unloaded(object sender, RoutedEventArgs e)
        {
            StopCamera();
        }

        private void StartCameraButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isGrabbing)
            {
                Log("相机已在采集中");
                return;
            }
            Task.Run((Action)ConnectAndStart);
        }

        private void StopCameraButton_Click(object sender, RoutedEventArgs e)
        {
            Task.Run((Action)StopCamera);
        }

        private void SaveImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (m_pCSI == null || !_isGrabbing)
            {
                Log("相机未在采集中，无法保存图像");
                return;
            }
            Task.Run((Action)SaveImage);
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            Dispatcher.Invoke((Action)(() => { LogTextBox.Clear(); }));
        }

        // ========== 核心相机操作（MVS 5.0.1 确认的 API） ==========

        private void ConnectAndStart()
        {
            try
            {
                UpdateStatus("正在枚举设备...");

                // 1. 枚举设备（静态方法）
                MyCamera.MV_CC_DEVICE_INFO_LIST deviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
                int nRet = MyCamera.MV_CC_EnumDevices_NET(_targetDeviceType, ref deviceList);
                if (nRet != 0 || deviceList.nDeviceNum == 0)
                {
                    UpdateStatus("未找到设备");
                    Log(string.Format("枚举失败: 0x{0:X}, 设备数={1}", nRet, deviceList.nDeviceNum));
                    return;
                }
                Log(string.Format("找到 {0} 个设备", deviceList.nDeviceNum));

                // 2. 按序列号匹配设备（对照 shankeda HKCameraHardwareAdapter）
                bool found = false;
                MyCamera.MV_CC_DEVICE_INFO targetDevice = new MyCamera.MV_CC_DEVICE_INFO();

                for (int i = 0; i < deviceList.nDeviceNum; i++)
                {
                    // pDeviceInfo[i] 是 IntPtr，直接 Marshal（对照 shankeda CameraOperator）
                    IntPtr pDevice =
                        Marshal.ReadIntPtr(deviceList.pDeviceInfo, i * IntPtr.Size);
                    MyCamera.MV_CC_DEVICE_INFO device =
                        (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(
                            pDevice,
                            typeof(MyCamera.MV_CC_DEVICE_INFO));

                    string serial = GetDeviceSerial(device);
                    Log(string.Format("  设备[{0}]: nTLayerType={1}, 序列号={2}", i, device.nTLayerType, serial));

                    if (serial == _targetSerial)
                    {
                        targetDevice = device;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    UpdateStatus("未找到匹配设备");
                    Log(string.Format("未找到序列号={0}的设备", _targetSerial));
                    return;
                }

                // 3. 创建设备（实例方法）
                UpdateStatus("正在创建设备...");
                m_pCSI = new MyCamera();
                nRet = m_pCSI.MV_CC_CreateDevice_NET(ref targetDevice);
                if (nRet != 0)
                {
                    UpdateStatus("创建设备失败");
                    Log(string.Format("CreateDevice失败: 0x{0:X}", nRet));
                    m_pCSI = null;
                    return;
                }

                // 4. 打开设备（MVS 5.0.1: 2参数）
                UpdateStatus("正在打开设备...");
                nRet = m_pCSI.MV_CC_OpenDevice_NET(
                    MyCamera.MV_ACCESS_MODE.MV_ACCESS_Exclusive,  // 独占访问
                    0);                                              // nSwitchoverKey
                if (nRet != 0)
                {
                    UpdateStatus("打开设备失败");
                    Log(string.Format("OpenDevice失败: 0x{0:X}", nRet));
                    m_pCSI.MV_CC_DestroyDevice_NET();
                    m_pCSI = null;
                    return;
                }

                // 5. 获取图像缓存大小（对照 CSDN MVS 5.0.1 示例）
                MyCamera.MVCC_INTVALUE stParam = new MyCamera.MVCC_INTVALUE();
                nRet = m_pCSI.MV_CC_GetIntValue_NET("PayloadSize", ref stParam);
                if (nRet != 0)
                {
                    Log(string.Format("获取PayloadSize失败: 0x{0:X}", nRet));
                }
                else
                {
                    uint payloadSize = stParam.nCurValue;
                    _convertBufferSize = payloadSize * 3 + 2048;  // 预留 BMP 头
                    _convertBuffer = new byte[_convertBufferSize];
                    Log(string.Format("PayloadSize={0}", payloadSize));
                }

                // 6. 开始采集（MVS 5.0.1: 0参数）
                UpdateStatus("正在开始采集...");
                nRet = m_pCSI.MV_CC_StartGrabbing_NET();
                if (nRet != 0)
                {
                    UpdateStatus("开始采集失败");
                    Log(string.Format("StartGrabbing失败: 0x{0:X}", nRet));
                    m_pCSI.MV_CC_CloseDevice_NET();
                    m_pCSI.MV_CC_DestroyDevice_NET();
                    m_pCSI = null;
                    return;
                }

                _isGrabbing = true;
                _frameCount = 0;
                UpdateStatus("采集中");
                Log("相机连接成功，开始采集");

                // 7. 启动显示线程（MV_CC_Display_NET 需持续调用）
                StartDisplayThread();
            }
            catch (Exception ex)
            {
                UpdateStatus("连接失败");
                Log(string.Format("异常: {0}", ex.Message));
                CleanupCamera();
            }
        }

        private void StopCamera()
        {
            try
            {
                StopDisplayThread();

                if (m_pCSI != null)
                {
                    if (_isGrabbing)
                    {
                        m_pCSI.MV_CC_StopGrabbing_NET();
                        _isGrabbing = false;
                    }
                    m_pCSI.MV_CC_CloseDevice_NET();
                    m_pCSI.MV_CC_DestroyDevice_NET();
                    m_pCSI = null;
                }
                UpdateStatus("已断开");
                Log("相机已停止并断开");
            }
            catch (Exception ex)
            {
                Log(string.Format("停止异常: {0}", ex.Message));
            }
        }

        private void CleanupCamera()
        {
            try
            {
                if (m_pCSI != null)
                {
                    if (_isGrabbing)
                    {
                        m_pCSI.MV_CC_StopGrabbing_NET();
                        _isGrabbing = false;
                    }
                    m_pCSI.MV_CC_CloseDevice_NET();
                    m_pCSI.MV_CC_DestroyDevice_NET();
                    m_pCSI = null;
                }
            }
            catch { }
        }

        // ========== 显示线程（对照 shankeda: 持续调用 MV_CC_Display_NET） ==========

        private void StartDisplayThread()
        {
            _displayRunning = true;
            _displayThread = new Thread(() =>
            {
                while (_displayRunning && m_pCSI != null)
                {
                    try
                    {
                        if (CameraPictureBox.IsHandleCreated)
                        {
                            // MV_CC_Display_NET(1参数 IntPtr hWnd)，对照 shankeda CameraOperator
                            m_pCSI.MV_CC_Display_NET(CameraPictureBox.Handle);
                        }
                    }
                    catch { }
                    Thread.Sleep(30);
                }
            });
            _displayThread.IsBackground = true;
            _displayThread.Start();
        }

        private void StopDisplayThread()
        {
            _displayRunning = false;
            if (_displayThread != null && _displayThread.IsAlive)
            {
                _displayThread.Join(1000);
            }
            _displayThread = null;
        }

        // ========== 保存图像（MVS 5.0.1: GetImageBuffer + ConvertPixelType + SaveImageToFile） ==========

        private void SaveImage()
        {
            if (m_pCSI == null)
            {
                Log("相机未初始化");
                return;
            }

            try
            {
                // 1. 获取一帧（GetImageBuffer 方式，对照 CSDN MVS 5.0.1 示例）
                _stImageOut = new MyCamera.MV_FRAME_OUT();
                int nRet = m_pCSI.MV_CC_GetImageBuffer_NET(ref _stImageOut, 1000);
                if (nRet != 0)
                {
                    Log(string.Format("获取图像失败: 0x{0:X}", nRet));
                    return;
                }

                IntPtr pSrcData = _stImageOut.pImageAddr;
                uint nWidth = _stImageOut.stFrameInfo.nWidth;
                uint nHeight = _stImageOut.stFrameInfo.nHeight;
                uint nFrameLen = _stImageOut.stFrameInfo.nFrameLen;
                MyCamera.MvGvspPixelType enSrcPixelType = _stImageOut.stFrameInfo.enPixelType;

                Log(string.Format("获取帧: {0}x{1}, PixelType={2}, FrameLen={3}",
                    nWidth, nHeight, enSrcPixelType, nFrameLen));

                // 2. 分配转换缓存
                if (_convertBuffer == null || _convertBuffer.Length < nFrameLen * 3 + 2048)
                {
                    _convertBufferSize = nFrameLen * 3 + 2048;
                    _convertBuffer = new byte[_convertBufferSize];
                }
                IntPtr pDstBuffer = Marshal.UnsafeAddrOfPinnedArrayElement(_convertBuffer, 0);

                // 3. 像素格式转换（对照 CSDN MVS 5.0.1 RawImageData2Bitmap）
                MyCamera.MvGvspPixelType enDstPixelType;
                if (IsMonoPixelFormat(enSrcPixelType))
                {
                    enDstPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8;
                }
                else if (IsColorPixelFormat(enSrcPixelType))
                {
                    enDstPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_RGB8_Packed;
                }
                else
                {
                    Log(string.Format("不支持的像素格式: {0}", enSrcPixelType));
                    m_pCSI.MV_CC_FreeImageBuffer_NET(ref _stImageOut);
                    return;
                }

                MyCamera.MV_PIXEL_CONVERT_PARAM stConverPixelParam = new MyCamera.MV_PIXEL_CONVERT_PARAM
                {
                    nWidth = nWidth,
                    nHeight = nHeight,
                    pSrcData = pSrcData,
                    nSrcDataLen = nFrameLen,
                    enSrcPixelType = enSrcPixelType,
                    enDstPixelType = enDstPixelType,
                    pDstBuffer = pDstBuffer,
                    nDstBufferSize = _convertBufferSize
                };

                nRet = m_pCSI.MV_CC_ConvertPixelType_NET(ref stConverPixelParam);
                if (nRet != 0)
                {
                    Log(string.Format("像素转换失败: 0x{0:X}", nRet));
                    m_pCSI.MV_CC_FreeImageBuffer_NET(ref _stImageOut);
                    return;
                }

                // 4. 生成文件名和保存路径
                string fileName = string.Format("Capture_{0:yyyyMMdd_HHmmss}.bmp",
                    DateTime.Now);
                string saveDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "CameraCaptures");
                if (!Directory.Exists(saveDir))
                {
                    Directory.CreateDirectory(saveDir);
                }
                string savePath = Path.Combine(saveDir, fileName);

                // 5. 使用 MV_SaveImageToFile_NET 保存（对照 CSDN MVS 5.0.1 示例）
                // pData 为 byte[]，strImagePath 为 string（MVS 5.0.1 对应）
                byte[] imageData = new byte[nFrameLen * 3 + 2048];  // 预分配
                Array.Copy(_convertBuffer, imageData, (int)Math.Min(imageData.Length, _convertBuffer.Length));

                // 根据目标格式确定图像类型
                MyCamera.MV_SAVE_IMG_TYPE enImageType;
                if (enDstPixelType == MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8)
                {
                    enImageType = MyCamera.MV_SAVE_IMG_TYPE.MV_SAVE_IMG_BMP;
                }
                else
                {
                    enImageType = MyCamera.MV_SAVE_IMG_TYPE.MV_SAVE_IMG_BMP;
                }

                // 构造保存参数（对照 CSDN MV_SAVE_IMG_TO_FILE_PARAM）
                MyCamera.MV_SAVE_IMG_TO_FILE_PARAM stSaveFileParam = new MyCamera.MV_SAVE_IMG_TO_FILE_PARAM
                {
                    enImageType = enImageType,
                    nWidth = nWidth,
                    nHeight = nHeight,
                    pData = _convertBuffer,  // MVS 5.0.1: byte[]
                    nDataLen = (uint)_convertBuffer.Length,
                    strImagePath = savePath    // MVS 5.0.1: string
                };

                nRet = m_pCSI.MV_SaveImageToFile_NET(ref stSaveFileParam);
                if (nRet == 0)
                {
                    Log(string.Format("图像已保存: {0}", savePath));
                }
                else
                {
                    Log(string.Format("保存失败: 0x{0:X}", nRet));
                    // 保存失败时，尝试直接写文件
                    WriteBitmapDirectly(savePath, nWidth, nHeight, enDstPixelType, _convertBuffer);
                }

                // 6. 释放缓存（必须调用）
                m_pCSI.MV_CC_FreeImageBuffer_NET(ref _stImageOut);
            }
            catch (Exception ex)
            {
                Log(string.Format("保存异常: {0}", ex.Message));
            }
        }

        /// <summary>
        /// 直接写 BMP 文件（兜底方案）
        /// </summary>
        private void WriteBitmapDirectly(string path, uint nWidth, uint nHeight,
            MyCamera.MvGvspPixelType enPixelType, byte[] buffer)
        {
            try
            {
                int bytesPerPixel = (enPixelType == MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8) ? 1 : 3;
                int stride = (int)(nWidth * (uint)bytesPerPixel);
                // BMP 行对齐（4字节对齐）
                int strideAligned = ((stride + 3) / 4) * 4;

                using (FileStream fs = new FileStream(path, FileMode.Create))
                using (BinaryWriter bw = new BinaryWriter(fs))
                {
                    // BMP 文件头 (14 bytes)
                    bw.Write((byte)'B'); bw.Write((byte)'M');
                    uint fileSize = 14 + 40 + (uint)(strideAligned * nHeight);
                    bw.Write(fileSize);
                    bw.Write((uint)0);         // reserved
                    bw.Write((uint)14 + 40);  // pixel data offset

                    // DIB 头 (40 bytes)
                    bw.Write((uint)40);        // header size
                    bw.Write((int)nWidth);     // width
                    bw.Write((int)nHeight);    // height (positive = bottom-up)
                    bw.Write((ushort)1);       // planes
                    bw.Write((ushort)(bytesPerPixel * 8)); // bits per pixel
                    bw.Write((uint)0);        // compression (none)
                    bw.Write((uint)(strideAligned * nHeight)); // image size
                    bw.Write((int)0); bw.Write((int)0); // ppm
                    bw.Write((uint)0); bw.Write((uint)0); // colors

                    // 像素数据（从上到下写入）
                    for (int row = (int)nHeight - 1; row >= 0; row--)
                    {
                        int rowStart = row * stride;
                        for (int col = 0; col < stride; col++)
                        {
                            if (rowStart + col < buffer.Length)
                            {
                                bw.Write(buffer[rowStart + col]);
                            }
                            else
                            {
                                bw.Write((byte)0);
                            }
                        }
                        // 行对齐填充
                        int paddedStride = strideAligned - stride;
                        for (int p = 0; p < paddedStride; p++)
                        {
                            bw.Write((byte)0);
                        }
                    }
                }
                Log(string.Format("BMP 已直接写入: {0}", path));
            }
            catch (Exception ex)
            {
                Log(string.Format("BMP写入失败: {0}", ex.Message));
            }
        }

        // ========== 辅助方法 ==========

        /// <summary>
        /// 获取设备序列号（适配 MVS 5.0.1 byte[] chSerialNumber）
        /// </summary>
        private string GetDeviceSerial(MyCamera.MV_CC_DEVICE_INFO device)
        {
            try
            {
                if (device.nTLayerType == MyCamera.MV_GIGE_DEVICE)
                {
                    // stGigEInfo 是 byte[]，Marshal 到 MV_GIGE_DEVICE_INFO
                    IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(
                        device.SpecialInfo.stGigEInfo, 0);
                    MyCamera.MV_GIGE_DEVICE_INFO gigeInfo =
                        (MyCamera.MV_GIGE_DEVICE_INFO)Marshal.PtrToStructure(
                            buffer, typeof(MyCamera.MV_GIGE_DEVICE_INFO));

                    // chSerialNumber 是 byte[]（对照 CSDN MVS 5.0.1 示例）
                    return Encoding.ASCII.GetString(gigeInfo.chSerialNumber).TrimEnd('\0');
                }
                else if (device.nTLayerType == MyCamera.MV_USB_DEVICE)
                {
                    IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(
                        device.SpecialInfo.stUsb3VInfo, 0);
                    MyCamera.MV_USB3_DEVICE_INFO usbInfo =
                        (MyCamera.MV_USB3_DEVICE_INFO)Marshal.PtrToStructure(
                            buffer, typeof(MyCamera.MV_USB3_DEVICE_INFO));

                    return Encoding.ASCII.GetString(usbInfo.chSerialNumber).TrimEnd('\0');
                }
            }
            catch (Exception ex)
            {
                Log(string.Format("获取序列号异常: {0}", ex.Message));
            }
            return "";
        }

        /// <summary>
        /// 判断是否为单色像素格式（对照 CSDN MVS 5.0.1）
        /// </summary>
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

        /// <summary>
        /// 判断是否为彩色像素格式（对照 CSDN MVS 5.0.1）
        /// </summary>
        private bool IsColorPixelFormat(MyCamera.MvGvspPixelType enType)
        {
            switch (enType)
            {
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_RGB8_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BGR8_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_RGBA8_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BGRA8_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_YUV422_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_YUV422_YUYV_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGR8:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerRG8:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGB8:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerBG8:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGR10:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerRG10:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGB10:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerBG10:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGR12:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerRG12:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGB12:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerBG12:
                    return true;
                default:
                    return false;
            }
        }

        private void UpdateStatus(string status)
        {
            Dispatcher.Invoke((Action)(() => { StatusText.Text = status; }));
        }

        private void Log(string msg)
        {
            Dispatcher.Invoke((Action)(() =>
            {
                string time = DateTime.Now.ToString("HH:mm:ss");
                LogTextBox.AppendText(string.Format("[{0}] {1}\n", time, msg));
                LogTextBox.ScrollToEnd();
            }));
        }
    }
}
