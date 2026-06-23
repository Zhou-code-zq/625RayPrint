using System;
using System.Reflection;
using System.Linq;

class DllInspector
{
    static void Main()
    {
        Assembly asm = Assembly.LoadFrom("MvCameraControl.Net.dll");
        var types = asm.GetTypes().OrderBy(t => t.FullName).ToArray();
        
        Console.WriteLine($"=== 共 {types.Length} 个类型 ===\n");
        
        foreach (var t in types)
        {
            Console.WriteLine($"\n--- {t.FullName} ---");
            
            // 字段
            var fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            foreach (var f in fields)
                Console.WriteLine($"  Field: {f.FieldType.Name} {f.Name}");
            
            // 属性
            var props = t.GetProperties();
            foreach (var p in props)
                Console.WriteLine($"  Prop:  {p.PropertyType.Name} {p.Name}");
            
            // 方法（只显示非继承的）
            var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => !m.IsSpecialName && m.DeclaringType == t)
                .OrderBy(m => m.Name)
                .ToArray();
            foreach (var m in methods)
            {
                var pstr = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
                Console.WriteLine($"  Method:{m.ReturnType.Name} {m.Name}({pstr})");
            }
        }
    }
}
