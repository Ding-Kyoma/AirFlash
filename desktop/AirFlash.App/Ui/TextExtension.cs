using System.Windows.Markup;
using AirFlash.Core;
namespace AirFlash.App.Ui;

[MarkupExtensionReturnType(typeof(string))]
public sealed class TextExtension(string english) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => L.Get(english);
}
