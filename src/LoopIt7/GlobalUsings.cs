// The project references both WPF and WinForms: WPF for the interface, WinForms purely for
// the tray icon, which has no first party WPF equivalent. Both bring a type called Brush,
// Application, Color and friends. These aliases settle every collision in favour of WPF, and
// the two files that genuinely need the WinForms or GDI type name it in full.

global using Application = System.Windows.Application;
global using Binding = System.Windows.Data.Binding;
global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using Color = System.Windows.Media.Color;
global using Colors = System.Windows.Media.Colors;
global using FontFamily = System.Windows.Media.FontFamily;
global using MessageBox = System.Windows.MessageBox;
global using MouseEventArgs = System.Windows.Input.MouseEventArgs;
global using Point = System.Windows.Point;
global using Size = System.Windows.Size;
global using UserControl = System.Windows.Controls.UserControl;
