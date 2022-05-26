using System.Windows.Controls;
using System.Windows.Data;

namespace BaseLmPlugin
{
    /// <summary>
    /// Interaction logic for AddSteamKey.xaml
    /// </summary>
    public partial class AddSteamKey : UserControl
    {
        public AddSteamKey()
        {
            InitializeComponent();
        }

        public AddSteamKey(bool showIdField) : this()
        {
            if (!showIdField)
            {
                idFieldDock.Visibility = System.Windows.Visibility.Collapsed;
                BindingOperations.ClearBinding(idField, TextBox.TextProperty);
            }
        }
    }
}