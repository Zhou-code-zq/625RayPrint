using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.IO;
using MvCamCtrl.NET;

namespace WpfApp1
{
    public partial class VisionInspectionPage : Page
    {
        // 海康SDK相关
        private MyCamera m_pMyCamera = new MyCamera();
        private MyCamera.MV_CC_DEVICE_INFO_LIST m_stDeviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
        private bool m_bGrabbing = false;
        private uint m_nBufSizeForSaveImage = 0;
        private byte[] m_pBufForSaveImage = null;
        private DispatcherTimer m_Timer;

        // 配置参数
        private string m_strDeviceSerial = "";
        private uint m_nDeviceType = 0;

        public VisionInspectionPage()
        {
            InitializeComponent();
        }

        // 从MainWindow设置相机配置
        public void SetCameraConfig(string deviceSerial, uint deviceType)
        {
            m_strDeviceSerial = deviceSerial;
            m_nDeviceType = deviceType;
            LogTextBox.Text = "";
            AddLog("相机配置: 序列号=" + deviceSerial + ", 类型=" + deviceType);
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            AddLog("页面加载完成");
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            // 页面卸载时停止采集
            if (m_bGrabbing)
            {
                StopGrabbing();
            }
        }

        private void StartCameraButton_Click(object sender, RoutedEventArgs e)
        {
            if (m_bGrabbing)
            {
                // 停止采集
                StopGrabbing();
                StartCameraButton.Content = "开始采集";
                AddLog("已停止采集");
            }
            else
            {
                // 开始采集
                AddLog("正在开始采集...");
                int nRet = ConnectAndStartGrabbing();
                if (nRet == 0)
                {
                    m_bGrabbing = true;
                    StartCameraButton.Content = "停止采集";
                    AddLog("采集已开始");
                }
            }
        }

        private void SaveImageButton_Click(object sender, RoutedEventArgs e)
        {
            SaveCurrentImage();
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            LogTextBox.Text = "";
        }

        // 连接相机并开始采集
        private int ConnectAndStartGrabbing()
        {
            // 初始化SDK
            int nRet = MyCamera.MV_CC_Initialize_NET();
            if (MyCamera.MV_OK != nRet)
            {
                AddLog("SDK初始化失败，错误码：" + nRet);
                return nRet;
            }

            // 枚举设备
            nRet = MyCamera.MV_CC_EnumDevices_NET(MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE, ref m_stDeviceList);
            if (MyCamera.MV_OK != nRet)
            {
                AddLog("枚举设备失败，错误码：" + nRet);
                MyCamera.MV_CC_Finalize_NET();
                return nRet;
            }

            AddLog("发现设备数量：" + m_stDeviceList.nDeviceNum);

            // 查找目标设备
            MyCamera.MV_CC_DEVICE_INFO deviceInfo = new MyCamera.MV_CC_DEVICE_INFO();
            bool deviceFound = false;
            int deviceIndex = -1;

            for (uint i = 0; i < m_stDeviceList.nDeviceNum; i++)
            {
                IntPtr pDeviceInfo = Marshal.UnsafeAddrOfPinnedArrayElement(m_stDeviceList.pDeviceInfo, (int)i);
                deviceInfo = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo, typeof(MyCamera.MV_CC_DEVICE_INFO));

                // 获取设备序列号
                string serial = GetDeviceSerial(deviceInfo);
                AddLog("设备" + i + "序列号：" + serial);

                if (serial == m_strDeviceSerial)
                {
                    deviceFound = true;
                    deviceIndex = (int)i;
                    AddLog("找到目标设备！");
                    break;
                }
            }

            if (!deviceFound)
            {
                AddLog("未找到序列号为 " + m_strDeviceSerial + " 的设备");
                MyCamera.MV_CC_Finalize_NET();
                return -1;
            }

            // 获取设备信息
            IntPtr pDeviceInfo2 = Marshal.UnsafeAddrOfPinnedArrayElement(m_stDeviceList.pDeviceInfo, deviceIndex);
            MyCamera.MV_CC_DEVICE_INFO stDeviceInfo = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(pDeviceInfo2, typeof(MyCamera.MV_CC_DEVICE_INFO));

            // 创建设备
            nRet = m_pMyCamera.MV_CC_CreateDevice_NET(ref stDeviceInfo);
            if (MyCamera.MV_OK != nRet)
            {
                AddLog("创建设备失败，错误码：" + nRet);
                MyCamera.MV_CC_Finalize_NET();
                return nRet;
            }

            // 打开设备
            nRet = m_pMyCamera.MV_CC_OpenDevice_NET(MyCamera.MV_ACCESS_Exclusive, 0);
            if (MyCamera.MV_OK != nRet)
            {
                AddLog("打开设备失败，错误码：" + nRet);
                m_pMyCamera.MV_CC_DestroyDevice_NET();
                MyCamera.MV_CC_Finalize_NET();
                return nRet;
            }

            // 设置触发模式为连续采集
            nRet = m_pMyCamera.MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
            if (MyCamera.MV_OK != nRet)
            {
                AddLog("设置触发模式失败，错误码：" + nRet);
            }

            // 获取窗口句柄
            IntPtr hWnd = IntPtr.Zero;
            if (CameraImage.Child is System.Windows.Controls.Image image)
            {
                // Image控件不能直接显示，需要用其他方式
            }

            // 开始采集
            nRet = m_pMyCamera.MV_CC_StartGrabbing_NET();
            if (MyCamera.MV_OK != nRet)
            {
                AddLog("开始采集失败，错误码：" + nRet);
                m_pMyCamera.MV_CC_CloseDevice_NET();
                m_pMyCamera.MV_CC_DestroyDevice_NET();
                MyCamera.MV_CC_Finalize_NET();
                return nRet;
            }

            // 使用Display直接显示
            nRet = m_pMyCamera.MV_CC_Display_NET(hWnd);
            if (MyCamera.MV_OK != nRet)
            {
                AddLog("显示图像失败，错误码：" + nRet);
            }

            return 0;
        }

        // 获取设备序列号
        private string GetDeviceSerial(MyCamera.MV_CC_DEVICE_INFO deviceInfo)
        {
            try
            {
                if (deviceInfo.nTLayerType == MyCamera.MV_GIGE_DEVICE)
                {
                    IntPtr pGigEInfo = Marshal.UnsafeAddrOfPinnedArrayElement(deviceInfo.SpecialInfo.stGigEInfo, 0);
                    MyCamera.MV_GIGE_DEVICE_INFO stGigEInfo = (MyCamera.MV_GIGE_DEVICE_INFO)Marshal.PtrToStructure(pGigEInfo, typeof(MyCamera.MV_GIGE_DEVICE_INFO));
                    
                    // chSerialNumber 是 byte[32]，提取字符串
                    string serial = "";
                    for (int i = 0; i < 32; i++)
                    {
                        if (stGigEInfo.chSerialNumber[i] == 0) break;
                        serial += (char)stGigEInfo.chSerialNumber[i];
                    }
                    return serial.Trim();
                }
                else if (deviceInfo.nTLayerType == MyCamera.MV_USB_DEVICE)
                {
                    IntPtr pUsbInfo = Marshal.UnsafeAddrOfPinnedArrayElement(deviceInfo.SpecialInfo.stUsb3VInfo, 0);
                    MyCamera.MV_USB3_DEVICE_INFO stUsbInfo = (MyCamera.MV_USB3_DEVICE_INFO)Marshal.PtrToStructure(pUsbInfo, typeof(MyCamera.MV_USB3_DEVICE_INFO));
                    
                    string serial = "";
                    for (int i = 0; i < 32; i++)
                    {
                        if (stUsbInfo.chSerialNumber[i] == 0) break;
                        serial += (char)stUsbInfo.chSerialNumber[i];
                    }
                    return serial.Trim();
                }
            }
            catch (Exception ex)
            {
                AddLog("获取序列号异常：" + ex.Message);
            }
            return "";
        }

        // 停止采集
        private void StopGrabbing()
        {
            if (m_pMyCamera != null)
            {
                m_pMyCamera.MV_CC_StopGrabbing_NET();
                m_pMyCamera.MV_CC_CloseDevice_NET();
                m_pMyCamera.MV_CC_DestroyDevice_NET();
                MyCamera.MV_CC_Finalize_NET();
            }
            m_bGrabbing = false;
            AddLog("已停止采集并断开连接");
        }

        // 保存当前图像
        private void SaveCurrentImage()
        {
            if (!m_bGrabbing)
            {
                AddLog("请先开始采集");
                return;
            }

            try
            {
                // 获取图像宽度和高度
                MyCamera.MVCC_INTVALUE stWidth = new MyCamera.MVCC_INTVALUE();
                int nRet = m_pMyCamera.MV_CC_GetIntValue_NET("Width", ref stWidth);
                if (nRet != MyCamera.MV_OK)
                {
                    AddLog("获取图像宽度失败");
                    return;
                }

                MyCamera.MVCC_INTVALUE stHeight = new MyCamera.MVCC_INTVALUE();
                nRet = m_pMyCamera.MV_CC_GetIntValue_NET("Height", ref stHeight);
                if (nRet != MyCamera.MV_OK)
                {
                    AddLog("获取图像高度失败");
                    return;
                }

                uint nWidth = stWidth.nCurValue;
                uint nHeight = stHeight.nCurValue;
                uint nImageSize = nWidth * nHeight * 3; // RGB

                // 分配缓冲区
                if (m_pBufForSaveImage == null || m_nBufSizeForSaveImage < nImageSize)
                {
                    m_pBufForSaveImage = new byte[nImageSize];
                    m_nBufSizeForSaveImage = nImageSize;
                }

                // 获取一帧图像
                MyCamera.MV_FRAME_OUT stFrameOut = new MyCamera.MV_FRAME_OUT();
                nRet = m_pMyCamera.MV_CC_GetImageBuffer_NET(ref stFrameOut, 1000);
                if (nRet == MyCamera.MV_OK)
                {
                    // 复制图像数据
                    IntPtr pData = stFrameOut.pImageAddr;
                    Marshal.Copy(pData, m_pBufForSaveImage, 0, (int)stFrameOut.nFrameLen);

                    // 保存为BMP文件
                    string strPath = AppDomain.CurrentDomain.BaseDirectory + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bmp";
                    nRet = m_pMyCamera.MV_CC_SaveImageToFile_NET("BGRA", m_pBufForSaveImage, stFrameOut.nWidth, stFrameOut.nHeight, stFrameOut.pImageAddr, (uint)stFrameOut.nFrameLen, strPath);
                    
                    if (nRet == MyCamera.MV_OK)
                    {
                        AddLog("图像已保存到：" + strPath);
                    }
                    else
                    {
                        AddLog("保存图像失败，错误码：" + nRet);
                    }

                    // 释放图像缓存
                    m_pMyCamera.MV_CC_FreeImageBuffer_NET(ref stFrameOut);
                }
                else
                {
                    AddLog("获取图像失败，错误码：" + nRet);
                }
            }
            catch (Exception ex)
            {
                AddLog("保存图像异常：" + ex.Message);
            }
        }

        // 添加日志
        private void AddLog(string message)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            LogTextBox.Text += "[" + time + "] " + message + "\n";
            LogTextBox.ScrollToEnd();
        }
    }
}
