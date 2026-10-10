using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace BH3.Launcher;

internal sealed class GmWindow : Form
{
    private readonly WebView2 browser=new(){Dock=DockStyle.Fill,DefaultBackgroundColor=Color.FromArgb(15,18,23)};
    internal GmWindow(Uri address)
    {
        Text="崩坏 3 · GM 角色工具";StartPosition=FormStartPosition.CenterParent;Size=new(1380,920);MinimumSize=new(980,650);BackColor=Color.FromArgb(15,18,23);Controls.Add(browser);
        Shown+=async(_,_)=>
        {
            try
            {
                Theme.DarkTitle(this);
                var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(AppPaths.Data,"GMWebView"));
                if(IsDisposed)return;await browser.EnsureCoreWebView2Async(env);
                browser.CoreWebView2.Settings.AreDevToolsEnabled=false;
                browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled=false;
                browser.CoreWebView2.NewWindowRequested+=(_,e)=>e.Handled=true;
                browser.CoreWebView2.NavigationStarting+=(_,e)=>{if(!Uri.TryCreate(e.Uri,UriKind.Absolute,out var destination)||destination.GetLeftPart(UriPartial.Authority)!=address.GetLeftPart(UriPartial.Authority))e.Cancel=true;};
                browser.Source=address;
            }
            catch(Exception ex){MessageBox.Show(this,"无法打开 GM 窗口："+ex.Message+"\n请安装 Microsoft Edge WebView2 Runtime。","GM 工具",MessageBoxButtons.OK,MessageBoxIcon.Error);Close();}
        };
    }
}
