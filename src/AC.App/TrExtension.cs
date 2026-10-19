using System.Windows.Data;
using System.Windows.Markup;
using AC.Core;

namespace AC.App;

/// <summary>Текст интерфейса по ключу: {local:Tr ключ}. Привязка, а не строка, чтобы текст сменился вместе с языком.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
