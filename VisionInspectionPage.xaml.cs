using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MvCamCtrl.NET;

namespace WpfApp1
{
    public partial class VisionInspectionPage : Page
    {
        private MyCamera? _camera;
        private string _targetSerial = "";
        private uint _targetDeviceType = MyCamera.MV_GIGE_DEVICE;
        private bool _isGrabbing = false;
        private int _frameCount = 0;
        private Thread? _displayThread;
        private bool _displayRunning = false;
        private MyCamera.cbOutputdelegate? _imageCallback;

        public VisionInspectionPage()
        {
            InitializeComponent();
        }

        public void SetCameraConfig(string serialNo, uint sdkDeviceType)
        {
            _targetSerial = serialNo;
            _targetDeviceType = sdkDeviceType;
            Log($"相机配置: 序列号={serialNo}, 设备类型={sdkDeviceType}");
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
            Task.Run(() => ConnectAndStart());
        }

        private void StopCameraButton_Click(object sender, RoutedEventArgs e)
        {
            Task.Run(() => StopCamera());
        }

        private void SaveImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_camera == null || !_isGrabbing)
            {
                Log("相机未在采集中，无法保存图像");
                return;
            }
            Task.Run(() => SaveImage());
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            Dispatcher.Invoke(() => LogTextBox.Clear());
        }

        // ========== 核心相机操作 ==========

        private void ConnectAndStart()
        {
            try
            {
                UpdateStatus("正在枚举设备...");

                // 1. 枚举设备
                MyCamera.MV_CC_DEVICE_INFO_LIST deviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
                int nRet = MyCamera.MV_CC_EnumDevices_NET(_targetDeviceType, ref deviceList);
                if (nRet != 0 || deviceList.nDeviceNum == 0)
                {
                    UpdateStatus("未找到设备");
                    Log($"枚举失败: 0x{nRet:X}, 设备数={deviceList.nDeviceNum}");
                    return;
                }
                Log($"找到 {deviceList.nDeviceNum} 个设备");

                // 2. 按序列号匹配设备（参照 shankeda 模式）
                bool found = false;
                MyCamera.MV_CC_DEVICE_INFO targetDevice = new MyCamera.MV_CC_DEVICE_INFO();

                for (int i = 0; i < deviceList.nDeviceNum; i++)
                {
                    // pDeviceInfo 是 IntPtr[]，直接用索引取 IntPtr
                    MyCamera.MV_CC_DEVICE_INFO device =
                        (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(
                            deviceList.pDeviceInfo[i],
                            typeof(MyCamera.MV_CC_DEVICE_INFO));

                    string serial = GetDeviceSerial(device);
                    Log($"  设备[{i}]: nTLayerType={device.nTLayerType}, 序列号={serial}");

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
                    Log($"未找到序列号={_targetSerial}的设备");
                    return;
                }

                // 3. 创建设备
                UpdateStatus("正在创建设备...");
                _camera = new MyCamera();
                nRet = MyCamera.MV_CC_CreateDevice_NET(ref targetDevice);
                if (nRet != 0)
                {
                    UpdateStatus("创建设备失败");
                    Log($"CreateDevice失败: 0x{nRet:X}");
                    _camera = null;
                    return;
                }

                // 4. 打开设备 (MVS 5.0.1: 2个参数)
                UpdateStatus("正在打开设备...");
                nRet = _camera.MV_CC_OpenDevice_NET(1, 0);
                if (nRet != 0)
                {
                    UpdateStatus("打开设备失败");
                    Log($"OpenDevice失败: 0x{nRet:X}");
                    _camera.MV_CC_DestroyDevice_NET();
                    _camera = null;
                    return;
                }

                // 5. 注册图像回调
                _imageCallback = new MyCamera.cbOutputdelegate(ImageCallBack);
                nRet = _camera.MV_CC_RegisterImageCallBack_NET(_imageCallback, IntPtr.Zero);
                if (nRet != 0)
                {
                    Log($"RegisterImageCallBack警告: 0x{nRet:X}");
                }

                // 6. 开始采集 (MVS 5.0.1: 0个参数)
                UpdateStatus("正在开始采集...");
                nRet = _camera.MV_CC_StartGrabbing_NET();
                if (nRet != 0)
                {
                    UpdateStatus("开始采集失败");
                    Log($"StartGrabbing失败: 0x{nRet:X}");
                    _camera.MV_CC_CloseDevice_NET();
                    _camera.MV_CC_DestroyDevice_NET();
                    _camera = null;
                    return;
                }

                _isGrabbing = true;
                _frameCount = 0;
                UpdateStatus("采集中");
                Log("相机连接成功，开始采集");

                // 7. 启动显示线程
                StartDisplayThread();
            }
            catch (Exception ex)
            {
                UpdateStatus("连接失败");
                Log($"异常: {ex.Message}");
                CleanupCamera();
            }
        }

        private void StopCamera()
        {
            try
            {
                StopDisplayThread();

                if (_camera != null)
                {
                    if (_isGrabbing)
                    {
                        _camera.MV_CC_StopGrabbing_NET();
                        _isGrabbing = false;
                    }
                    _camera.MV_CC_CloseDevice_NET();
                    _camera.MV_CC_DestroyDevice_NET();
                    _camera = null;
                }
                UpdateStatus("已断开");
                Log("相机已停止并断开");
            }
            catch (Exception ex)
            {
                Log($"停止异常: {ex.Message}");
            }
        }

        private void CleanupCamera()
        {
            try
            {
                if (_camera != null)
                {
                    if (_isGrabbing)
                    {
                        _camera.MV_CC_StopGrabbing_NET();
                        _isGrabbing = false;
                    }
                    _camera.MV_CC_CloseDevice_NET();
                    _camera.MV_CC_DestroyDevice_NET();
                    _camera = null;
                }
            }
            catch { }
        }

        // ========== 图像回调 ==========

        private void ImageCallBack(IntPtr pData, ref MyCamera.MV_FRAME_OUT_INFO_EX pFrameInfo, IntPtr pUser)
        {
            _frameCount++;
            Dispatcher.Invoke(() =>
            {
                FrameCountText.Text = $"帧数: {_frameCount}";
            });
        }

        // ========== 显示线程（MV_CC_Display_NET 需要持续调用） ==========

        private void StartDisplayThread()
        {
            _displayRunning = true;
            _displayThread = new Thread(() =>
            {
                while (_displayRunning && _camera != null)
                {
                    try
                    {
                        if (CameraPictureBox.IsHandleCreated)
                        {
                            // MVS 5.0.1: 1个参数 (HWND)
                            _camera.MV_CC_Display_NET(CameraPictureBox.Handle);
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

        // ========== 保存图像 ==========

        private void SaveImage()
        {
            try
            {
                if (_camera == null) return;

                string fileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss}.bmp";

                MyCamera.MV_SAVE_IMAGE_PARAM_EX saveParam = new MyCamera.MV_SAVE_IMAGE_PARAM_EX();
                saveParam.enImageType = MyCamera.MV_SAVE_IAMGE_TYPE.MV_Image_Bmp;
                saveParam.enPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_RGB8_Packed;
                saveParam.nWidth = 0;
                saveParam.nHeight = 0;
                saveParam.nDataLen = 0;
                saveParam.pData = IntPtr.Zero;
                saveParam.pImagePath = fileName;
                saveParam.nImageLen = (uint)fileName.Length;

                int nRet = _camera.MV_CC_SaveImageEx_NET(ref saveParam);
                if (nRet == 0)
                {
                    Log($"图像已保存: {fileName}");
                }
                else
                {
                    Log($"保存失败: 0x{nRet:X}");
                }
            }
            catch (Exception ex)
            {
                Log($"保存异常: {ex.Message}");
            }
        }

        // ========== 辅助方法 ==========

        /// <summary>
        /// 获取设备序列号（参照 shankeda 模式，适配 MVS 5.0.1 byte[] 类型）
        /// </summary>
        private string GetDeviceSerial(MyCamera.MV_CC_DEVICE_INFO device)
        {
            try
            {
                if (device.nTLayerType == MyCamera.MV_GIGE_DEVICE)
                {
                    // stGigEInfo 是 byte[]，需要 Marshal 到 MV_GIGE_DEVICE_INFO
                    IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(
                        device.SpecialInfo.stGigEInfo, 0);
                    MyCamera.MV_GIGE_DEVICE_INFO gigeInfo =
                        (MyCamera.MV_GIGE_DEVICE_INFO)Marshal.PtrToStructure(
                            buffer, typeof(MyCamera.MV_GIGE_DEVICE_INFO));

                    // MVS 5.0.1: chSerialNumber 是 byte[]
                    byte[] serialBytes = gigeInfo.chSerialNumber;
                    return Encoding.ASCII.GetString(serialBytes).TrimEnd('\0');
                }
                else if (device.nTLayerType == MyCamera.MV_USB_DEVICE)
                {
                    IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(
                        device.SpecialInfo.stUsb3VInfo, 0);
                    MyCamera.MV_USB3_DEVICE_INFO usbInfo =
                        (MyCamera.MV_USB3_DEVICE_INFO)Marshal.PtrToStructure(
                            buffer, typeof(MyCamera.MV_USB3_DEVICE_INFO));

                    byte[] serialBytes = usbInfo.chSerialNumber;
                    return Encoding.ASCII.GetString(serialBytes).TrimEnd('\0');
                }
            }
            catch (Exception ex)
            {
                Log($"获取序列号异常: {ex.Message}");
            }
            return "";
        }

        private void UpdateStatus(string status)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = status;
            });
        }

        private void Log(string msg)
        {
            Dispatcher.Invoke(() =>
            {
                string time = DateTime.Now.ToString("HH:mm:ss");
                LogTextBox.AppendText($"[{time}] {msg}\n");
                LogTextBox.ScrollToEnd();
            });
        }
    }
}
