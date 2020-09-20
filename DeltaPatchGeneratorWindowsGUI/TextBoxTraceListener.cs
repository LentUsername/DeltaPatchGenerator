using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace DeltaPatchGeneratorWindowsGUI
{
    class TextBoxTraceListener : TextWriterTraceListener
    {
        readonly TextBox textBox;
        readonly SynchronizationContext context;

        public TextBoxTraceListener(TextBox textBox, SynchronizationContext context)
        {
            this.textBox = textBox;
            this.context = context;
        }
        public override void WriteLine(string message)
        {
            context.Send(state =>
            {
                textBox.AppendText($"{message}{Environment.NewLine}");
                if (textBox.ExtentHeight - textBox.VerticalOffset - textBox.ViewportHeight < 40)
                    textBox.ScrollToEnd();
            }, null);
        }
    }
}
