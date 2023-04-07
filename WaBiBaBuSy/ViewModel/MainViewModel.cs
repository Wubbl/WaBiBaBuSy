using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WaBiBaBuSy.ViewModel
{
    internal class MainViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void RaisePropertyChangedEvent(string propName)
        {
            if (PropertyChanged != null)
            {
                PropertyChanged(this, new PropertyChangedEventArgs(propName));
            }
        }

        public MainViewModel()
        {
            _statusText = "Status: Busen";
            _port = 51234;
        }

        private string _statusText;

        public string StatusText
        {
            get { return _statusText; }
            set { _statusText = value; RaisePropertyChangedEvent("StatusText"); }
        }

        private int _port;

        public int Port
        {
            get { return _port; }
            set { _port = value; }
        }


    }
}
