﻿using System;
using System.Windows.Data;

namespace MetaMorpheusGUI
{
    public class BooleanInverter : IValueConverter
    {
        #region Public Methods

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return !(bool)value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return (bool)value;
        }

        #endregion Public Methods
    }
}