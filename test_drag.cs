using System;
using Avalonia.Input;
class Program {
    static void Main() {
        foreach(var prop in typeof(DragEventArgs).GetProperties()) {
            Console.WriteLine(prop.Name);
        }
    }
}
