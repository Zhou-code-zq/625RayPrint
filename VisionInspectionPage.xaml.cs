using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using MvCamCtrl.NET;

namespace WpfApp1
{
    public partial class VisionInspectionPage : Page
    {
        // ── 相机核心 ──
        private MyCamera _camera;
        private IntPtr _cameraHandle = IntPtr.Zero;
        private MyCamera.cbOutputdelegate _imageCallback;
        private MyCamera.cbExceptiondelegate _exceptionCallback;

        // ── 配置 ──
        private string _targetSerial = "";
        private uint _targetDeviceType = MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE;

        // ── 状态 ──
        private bool _isGrabbing = false;
        private bool _isConnected = false;
        private int _frameCount = 0;
        private readonly object _lockObj = new object();

        // ── 显示控件 ──
        private System.Windows.Forms.PictureBox _pictureBox;

        public VisionInspectionPage()
        {
            InitializeComponent();

            // 创建 WinForms PictureBox 用于相机显示
            _pictureBox = new System.Windows.Forms.PictureBox
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                BackColor = System.Drawing.Color.Black,
                SizeMode = System.Windows.Forms.PictureBoxSizeMode.Zoom
            };
            CameraDisplay.Child = _pictureBox;
        }

        // ═══════════════════════════════════════════════
        //  外部调用：设置相机配置
        // ═══════════════════════════════════════════════
        public void SetCameraConfig(string serial, uint deviceType)
        {
            _targetSerial = serial ?? "";
            _targetDeviceType = deviceType;
            AppendLog($"[配置] 序列号={_targetSerial}, 设备类型={deviceType}");
        }

        // ═══════════════════════════════════════════════
        //  页面加载 / 卸载
        // ═══════════════════════════════════════════════
        private void VisionInspectionPage_Loaded(object sender, RoutedEventArgs e)
        {
            AppendLog("[系统] 视觉检测页面已加载");
        }

        private void VisionInspectionPage_Unloaded(object sender, RoutedEventArgs e)
        {
            DisconnectCamera();
            AppendLog("[系统] 视觉检测页面已卸载");
        }

        // ═══════════════════════════════════════════════
        //  开始采集 = 连接 + 开始采集
        // ═══════════════════════════════════════════════
        private void StartCameraButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnected && _isGrabbing)
            {
                AppendLog("[警告] 相机已在采集中");
                return;
            }

            StartCameraButton.IsEnabled = false;
            UpdateStatus("连接中...", "#FFA500");

            Task.Run(() =>
            {
                try
                {
                    if (!_isConnected)
                    {
                        if (!ConnectCamera())
                        {
                            Dispatcher.Invoke(() =>
                            {
                                UpdateStatus("连接失败", "#EF4444");
                                StartCameraButton.IsEnabled = true;
                            });
                            return;
                        }
                    }

                    if (!StartGrabbing())
                    {
                        Dispatcher.Invoke(() =>
                        {
                            UpdateStatus("采集启动失败", "#EF4444");
                            StartCameraButton.IsEnabled = true;
                        });
                        return;
                    }

                    Dispatcher.Invoke(() =>
                    {
                        UpdateStatus("采集中", "#10B981");
                        StartCameraButton.IsEnabled = false;
                        StopCameraButton.IsEnabled = true;
                        SaveImageButton.IsEnabled = true;
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        UpdateStatus("异常", "#EF4444");
                        StartCameraButton.IsEnabled = true;
                        AppendLog($"[错误] {ex.Message}");
                    });
                }
            });
        }

        // ═══════════════════════════════════════════════
        //  停止采集 = 停止采集 + 断开
        // ═══════════════════════════════════════════════
        private void StopCameraButton_Click(object sender, RoutedEventArgs e)
        {
            StopCameraButton.IsEnabled = false;
            SaveImageButton.IsEnabled = false;

            Task.Run(() =>
            {
                try
                {
                    StopGrabbing();
                    DisconnectCamera();

                    Dispatcher.Invoke(() =>
                    {
                        UpdateStatus("已停止", "#888888");
                        StartCameraButton.IsEnabled = true;
                        StopCameraButton.IsEnabled = false;
                        SaveImageButton.IsEnabled = false;
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        AppendLog($"[错误] 停止失败: {ex.Message}");
                        StopCameraButton.IsEnabled = true;
                    });
                }
            });
        }

        // ═══════════════════════════════════════════════
        //  保存图像
        // ═══════════════════════════════════════════════
        private void SaveImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected || !_isGrabbing)
            {
                AppendLog("[警告] 请先开始采集");
                return;
            }

            SaveImageButton.IsEnabled = false;

            Task.Run(() =>
            {
                try
                {
                    string saveDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CapturedImages");
                    if (!Directory.Exists(saveDir))
                        Directory.CreateDirectory(saveDir);

                    string fileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss_fff}.bmp";
                    string filePath = Path.Combine(saveDir, fileName);

                    int nRet = _camera.MV_CC_SaveImage_NET(_cameraHandle, filePath);
                    if (MyCamera.MV_OK == nRet)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            AppendLog($"[保存] 图像已保存: {fileName}");
                            SaveImageButton.IsEnabled = true;
                        });
                    }
                    else
                    {
                        Dispatcher.Invoke(() =>
                        {
                            AppendLog($"[保存] 保存失败, 错误码: 0x{nRet:X8}");
                            SaveImageButton.IsEnabled = true;
                        });
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        AppendLog($"[保存] 异常: {ex.Message}");
                        SaveImageButton.IsEnabled = true;
                    });
                }
            });
        }

        // ═══════════════════════════════════════════════
        //  清除日志
        // ═══════════════════════════════════════════════
        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            LogTextBox.Clear();
        }

        // ═══════════════════════════════════════════════
        //  连接相机（仅通过序列号匹配）
        // ═══════════════════════════════════════════════
        private bool ConnectCamera()
        {
            try
            {
                Dispatcher.Invoke(() => AppendLog("[连接] 开始枚举设备..."));

                // 1. 枚举设备
                MyCamera.MV_CC_DEVICE_INFO_LIST deviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
                int nRet = MyCamera.MV_CC_EnumDevices_NET(_targetDeviceType, ref deviceList);
                if (MyCamera.MV_OK != nRet)
                {
                    Dispatcher.Invoke(() => AppendLog($"[连接] 枚举设备失败, 错误码: 0x{nRet:X8}"));
                    return false;
                }

                Dispatcher.Invoke(() => AppendLog($"[连接] 发现 {deviceList.nDeviceNum} 个设备"));

                if (deviceList.nDeviceNum == 0)
                {
                    Dispatcher.Invoke(() => AppendLog("[连接] 未发现任何设备"));
                    return false;
                }

                // 2. 遍历设备，按序列号匹配（不依赖 nTLayerType，尝试所有类型）
                bool found = false;
                MyCamera.MV_CC_DEVICE_INFO matchedDevice = new MyCamera.MV_CC_DEVICE_INFO();

                for (int i = 0; i < deviceList.nDeviceNum; i++)
                {
                    MyCamera.MV_CC_DEVICE_INFO device =
                        (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(
                            deviceList.pDeviceInfo[i], typeof(MyCamera.MV_CC_DEVICE_INFO));

                    string serial = "";

                    // 尝试 GigE
                    try
                    {
                        GCHandle handle = GCHandle.Alloc(device.SpecialInfo.stGigEInfo, GCHandleType.Pinned);
                        try
                        {
                            IntPtr ptr = handle.AddrOfPinnedObject();
                            MyCamera.MV_GIGE_DEVICE_INFO gigeInfo =
                                (MyCamera.MV_GIGE_DEVICE_INFO)Marshal.PtrToStructure(
                                    ptr, typeof(MyCamera.MV_GIGE_DEVICE_INFO));
                            serial = gigeInfo.chSerialNumber ?? "";
                        }
                        finally { handle.Free(); }
                    }
                    catch { }

                    // 尝试 USB3
                    if (string.IsNullOrEmpty(serial))
                    {
                        try
                        {
                            GCHandle handle = GCHandle.Alloc(device.SpecialInfo.stUsb3VInfo, GCHandleType.Pinned);
                            try
                            {
                                IntPtr ptr = handle.AddrOfPinnedObject();
                                MyCamera.MV_USB3_DEVICE_INFO usbInfo =
                                    (MyCamera.MV_USB3_DEVICE_INFO)Marshal.PtrToStructure(
                                        ptr, typeof(MyCamera.MV_USB3_DEVICE_INFO));
                                serial = usbInfo.chSerialNumber ?? "";
                            }
                            finally { handle.Free(); }
                        }
                        catch { }
                    }

                    Dispatcher.Invoke(() => AppendLog($"[连接] 设备[{i}]: 序列号={serial}"));

                    if (!string.IsNullOrEmpty(_targetSerial) && serial.Contains(_targetSerial))
                    {
                        matchedDevice = device;
                        found = true;
                        break;
                    }
                }

                // 如果没有匹配到序列号，使用第一个设备
                if (!found)
                {
                    if (string.IsNullOrEmpty(_targetSerial))
                    {
                        matchedDevice = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(
                            deviceList.pDeviceInfo[0], typeof(MyCamera.MV_CC_DEVICE_INFO));
                        Dispatcher.Invoke(() => AppendLog("[连接] 未指定序列号，使用第一个设备"));
                    }
                    else
                    {
                        Dispatcher.Invoke(() => AppendLog($"[连接] 未找到序列号包含 '{_targetSerial}' 的设备"));
                        return false;
                    }
                }

                // 3. 创建相机实例
                _camera = new MyCamera();

                // 4. 创建设备
                nRet = _camera.MV_CC_CreateDevice_NET(ref matchedDevice);
                if (MyCamera.MV_OK != nRet)
                {
                    Dispatcher.Invoke(() => AppendLog($"[连接] 创建设备失败, 错误码: 0x{nRet:X8}"));
                    _camera = null;
                    return false;
                }

                // 5. 打开设备
                nRet = _camera.MV_CC_OpenDevice_NET(IntPtr.Zero);
                if (MyCamera.MV_OK != nRet)
                {
                    Dispatcher.Invoke(() => AppendLog($"[连接] 打开设备失败, 错误码: 0x{nRet:X8}"));
                    _camera.MV_CC_DestroyDevice_NET();
                    _camera = null;
                    return false;
                }

                _cameraHandle = _camera.MV_CC_GetDeviceHandle_NET();

                // 6. 注册异常回调
                _exceptionCallback = new MyCamera.cbExceptiondelegate(CameraExceptionCallback);
                _camera.MV_CC_RegisterExceptionCallBack_NET(_exceptionCallback, IntPtr.Zero);

                // 7. 设置触发模式为连续
                _camera.MV_CC_SetEnumValue_NET("TriggerMode", 0);

                _isConnected = true;
                Dispatcher.Invoke(() =>
                {
                    AppendLog("[连接] 相机连接成功");
                    UpdateStatus("已连接", "#FFA500");
                });

                return true;
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AppendLog($"[连接] 异常: {ex.Message}"));
                return false;
            }
        }

        // ═══════════════════════════════════════════════
        //  开始采集
        // ═══════════════════════════════════════════════
        private bool StartGrabbing()
        {
            try
            {
                if (_camera == null || !_isConnected)
                    return false;

                // 注册图像回调（用于帧计数）
                _imageCallback = new MyCamera.cbOutputdelegate(ImageCallback);
                int nRet = _camera.MV_CC_RegisterImageCallBack_NET(_imageCallback, IntPtr.Zero);
                if (MyCamera.MV_OK != nRet)
                {
                    Dispatcher.Invoke(() => AppendLog($"[采集] 注册回调失败, 错误码: 0x{nRet:X8}"));
                }

                // 开始采集
                nRet = _camera.MV_CC_StartGrabbing_NET(IntPtr.Zero);
                if (MyCamera.MV_OK != nRet)
                {
                    Dispatcher.Invoke(() => AppendLog($"[采集] 开始采集失败, 错误码: 0x{nRet:X8}"));
                    return false;
                }

                _isGrabbing = true;
                _frameCount = 0;

                // 启动显示线程
                Thread displayThread = new Thread(DisplayThreadProcess)
                {
                    IsBackground = true,
                    Name = "CameraDisplay"
                };
                displayThread.Start();

                Dispatcher.Invoke(() => AppendLog("[采集] 开始采集成功"));
                return true;
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AppendLog($"[采集] 异常: {ex.Message}"));
                return false;
            }
        }

        // ═══════════════════════════════════════════════
        //  停止采集
        // ═══════════════════════════════════════════════
        private void StopGrabbing()
        {
            try
            {
                _isGrabbing = false;
                Thread.Sleep(100);

                if (_camera != null && _isConnected)
                {
                    _camera.MV_CC_StopGrabbing_NET();
                    Dispatcher.Invoke(() => AppendLog("[采集] 已停止采集"));
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AppendLog($"[采集] 停止异常: {ex.Message}"));
            }
        }

        // ═══════════════════════════════════════════════
        //  断开相机
        // ═══════════════════════════════════════════════
        private void DisconnectCamera()
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

                    if (_isConnected)
                    {
                        _camera.MV_CC_CloseDevice_NET();
                        _camera.MV_CC_DestroyDevice_NET();
                        _isConnected = false;
                    }

                    _camera = null;
                    _cameraHandle = IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AppendLog($"[断开] 异常: {ex.Message}"));
            }
        }

        // ═══════════════════════════════════════════════
        //  图像回调（仅用于帧计数）
        // ═══════════════════════════════════════════════
        private void ImageCallback(IntPtr pData, ref MyCamera.MV_FRAME_OUT_INFO pFrameInfo, IntPtr pUser)
        {
            // 仅计数，不访问帧数据
            int count = Interlocked.Increment(ref _frameCount);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                FrameCountText.Text = $"帧数: {count}";
            }), DispatcherPriority.Background);
        }

        // ═══════════════════════════════════════════════
        //  显示线程：使用 MV_CC_Display_NET
        // ═══════════════════════════════════════════════
        private void DisplayThreadProcess()
        {
            while (_isGrabbing && _isConnected)
            {
                try
                {
                    if (_camera != null && _pictureBox != null && _pictureBox.Handle != IntPtr.Zero)
                    {
                        _camera.MV_CC_Display_NET(_cameraHandle, _pictureBox.Handle);
                    }
                    Thread.Sleep(30);
                }
                catch
                {
                    break;
                }
            }
        }

        // ═══════════════════════════════════════════════
        //  异常回调
        // ═══════════════════════════════════════════════
        private void CameraExceptionCallback(uint nMsgType, IntPtr pUser)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AppendLog($"[异常] 相机异常, 类型: {nMsgType}");
                if (nMsgType == 0x8003 || nMsgType == 0x8004) // 断开连接
                {
                    StopGrabbing();
                    DisconnectCamera();
                    UpdateStatus("已断开", "#EF4444");
                    StartCameraButton.IsEnabled = true;
                    StopCameraButton.IsEnabled = false;
                    SaveImageButton.IsEnabled = false;
                }
            }));
        }

        // ═══════════════════════════════════════════════
        //  UI 辅助方法
        // ═══════════════════════════════════════════════
        private void UpdateStatus(string text, string color)
        {
            StatusText.Text = text;
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        }

        private void AppendLog(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            LogTextBox.AppendText($"[{timestamp}] {message}\n");
            LogTextBox.ScrollToEnd();
        }
    }
}
