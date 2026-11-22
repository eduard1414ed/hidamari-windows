// Windows Forms and WPF (here only for UI Automation) both name a Point and a Rectangle: the check means the pixel ones.
global using Point = System.Drawing.Point;
global using Rectangle = System.Drawing.Rectangle;
global using System.Runtime.CompilerServices;
// WPF leaves System.IO out of the implicit usings (its own Path is a shape)
global using System.IO;
global using Path = System.IO.Path;
