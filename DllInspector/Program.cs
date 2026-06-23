using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DllInspector
{
    class Program
    {
        static void Main(string[] args)
        {
            string dllPath = @"C:\Program Files (x86)\MVS\Development\DotNet\win64\MvCameraControl.Net.dll";
            Console.WriteLine($"正在加载: {dllPath}\n");

            Assembly asm;
            try
            {
                asm = Assembly.LoadFrom(dllPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载失败: {ex.Message}");
                Console.WriteLine("请确认 MVS 已安装，或将 MvCameraControl.Net.dll 复制到本目录");
                return;
            }

            var allTypes = asm.GetTypes().OrderBy(t => t.FullName).ToArray();
            Console.WriteLine($"=== DLL 共 {allTypes.Length} 个类型 ===\n");

            // 收集所有类/结构体/枚举
            var classTypes = allTypes.Where(t => t.IsClass && t.Namespace == "MvCamCtrl.NET").OrderBy(t => t.Name).ToArray();
            var enumTypes = allTypes.Where(t => t.IsEnum && t.Namespace == "MvCamCtrl.NET").OrderBy(t => t.Name).ToArray();
            var structTypes = allTypes.Where(t => t.IsValueType && !t.IsEnum && t.Namespace == "MvCamCtrl.NET").OrderBy(t => t.Name).ToArray();

            Console.WriteLine($"=== {classTypes.Length} 个类/接口 ===");
            foreach (var t in classTypes)
                Console.WriteLine($"  {t.Name} ({(t.IsInterface ? "interface" : "class")})");

            Console.WriteLine($"\n=== {enumTypes.Length} 个枚举 ===");
            foreach (var t in enumTypes)
            {
                Console.WriteLine($"  {t.Name}");
                var values = Enum.GetValues(t);
                foreach (var v in values)
                    Console.WriteLine($"    {v} = {(int)v}");
            }

            Console.WriteLine($"\n=== {structTypes.Length} 个结构体 ===");
            foreach (var t in structTypes)
            {
                Console.WriteLine($"\n  {t.Name}:");
                var fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
                foreach (var f in fields)
                    Console.WriteLine($"    {f.FieldType.Name} {f.Name}");
            }

            // MyCamera 类的方法详情
            Console.WriteLine("\n=== MyCamera 类所有方法（含参数）===");
            var myCamera = classTypes.FirstOrDefault(t => t.Name == "MyCamera");
            if (myCamera != null)
            {
                var methods = myCamera.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                    .Where(m => !m.IsSpecialName)
                    .OrderBy(m => m.Name)
                    .ToArray();
                foreach (var m in methods)
                {
                    var pstr = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({pstr})");
                }
            }
            else
            {
                Console.WriteLine("  未找到 MyCamera 类！");
            }

            // 找 MV_FRAME_OUT 和 MV_CC_DEVICE_INFO
            Console.WriteLine("\n=== MV_FRAME_OUT 结构体详情 ===");
            var mvFrameOut = structTypes.FirstOrDefault(t => t.Name.Contains("MV_FRAME_OUT"));
            if (mvFrameOut != null)
            {
                var fields = mvFrameOut.GetFields();
                foreach (var f in fields)
                    Console.WriteLine($"  {f.FieldType.Name} {f.Name}");
            }
            else Console.WriteLine("  未找到 MV_FRAME_OUT");

            Console.WriteLine("\n=== MV_CC_DEVICE_INFO 结构体详情 ===");
            var deviceInfo = structTypes.FirstOrDefault(t => t.Name.Contains("MV_CC_DEVICE_INFO"));
            if (deviceInfo != null)
            {
                var fields = deviceInfo.GetFields();
                foreach (var f in fields)
                    Console.WriteLine($"  {f.FieldType.Name} {f.Name}");
            }
            else Console.WriteLine("  未找到 MV_CC_DEVICE_INFO");

            // 搜索包含 Save 的类型
            Console.WriteLine("\n=== 包含 Save 相关方法的类型 ===");
            foreach (var t in allTypes.Where(t => t.Namespace == "MvCamCtrl.NET"))
            {
                var saveMethods = t.GetMethods().Where(m => m.Name.ToLower().Contains("save")).ToArray();
                if (saveMethods.Length > 0)
                {
                    Console.WriteLine($"\n  {t.Name}:");
                    foreach (var m in saveMethods)
                    {
                        var pstr = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                        Console.WriteLine($"    {m.ReturnType.Name} {m.Name}({pstr})");
                    }
                }
            }

            // 搜索包含 Open 的类型
            Console.WriteLine("\n=== 包含 Open 相关方法的类型 ===");
            foreach (var t in allTypes.Where(t => t.Namespace == "MvCamCtrl.NET"))
            {
                var methods = t.GetMethods().Where(m => m.Name.ToLower().Contains("open")).ToArray();
                if (methods.Length > 0)
                {
                    Console.WriteLine($"\n  {t.Name}:");
                    foreach (var m in methods)
                    {
                        var pstr = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                        Console.WriteLine($"    {m.ReturnType.Name} {m.Name}({pstr})");
                    }
                }
            }

            // 搜索包含 Convert 的类型
            Console.WriteLine("\n=== 包含 Convert/Pixel 相关方法的类型 ===");
            foreach (var t in allTypes.Where(t => t.Namespace == "MvCamCtrl.NET"))
            {
                var methods = t.GetMethods().Where(m => m.Name.ToLower().Contains("convert") || m.Name.ToLower().Contains("pixel")).ToArray();
                if (methods.Length > 0)
                {
                    Console.WriteLine($"\n  {t.Name}:");
                    foreach (var m in methods)
                    {
                        var pstr = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                        Console.WriteLine($"    {m.ReturnType.Name} {m.Name}({pstr})");
                    }
                }
            }

            // 搜索所有枚举
            Console.WriteLine("\n=== 所有枚举值（MV_ 开头）===");
            foreach (var t in enumTypes)
            {
                if (t.Name.StartsWith("MV_"))
                {
                    Console.WriteLine($"\n  {t.Name}:");
                    foreach (var v in Enum.GetValues(t))
                        Console.WriteLine($"    {v} = {(int)v}");
                }
            }

            Console.WriteLine("\n=== 检查完毕 ===");
            Console.WriteLine("按任意键退出...");
            Console.ReadKey();
        }
    }
}
