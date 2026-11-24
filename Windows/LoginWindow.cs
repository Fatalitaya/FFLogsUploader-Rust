using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace FFLogsPlugin.Windows;

public class LoginWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    
    private string email = string.Empty;
    private string password = string.Empty;
    private bool rememberMe = false;
    private string statusMessage = string.Empty;
    private bool loginError;
    private bool isLoggingIn = false;

    public LoginWindow(Plugin plugin)
        : base(Loc.Text("FFLogs Login") + "###FFLogsLogin", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(350, 0),
            MaximumSize = new Vector2(350, float.MaxValue)
        };

        // Load saved credentials if available
        if (plugin.Configuration.RememberCredentials)
        {
            email = plugin.Configuration.Email;
            password = plugin.Configuration.Password; // Transparently decrypted via DPAPI
            rememberMe = true;
        }
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.Text(Loc.Text("Log in to FFLogs"));
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.Text(Loc.Text("Email"));
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##email", ref email, 256);

        ImGui.Text(Loc.Text("Password"));
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##password", ref password, 256, ImGuiInputTextFlags.Password);

        ImGui.Checkbox(Loc.Text("Remember me"), ref rememberMe);

        ImGui.Spacing();

        ImGui.BeginDisabled(isLoggingIn);

        if (ImGui.Button(isLoggingIn ? Loc.Text("Logging in...") : Loc.Text("Login"), new Vector2(-1, 30)))
        {
            _ = DoLogin();
        }

        ImGui.EndDisabled();

        if (!string.IsNullOrEmpty(statusMessage))
        {
            ImGui.Spacing();
            var color = loginError
                ? new Vector4(1, 0.3f, 0.3f, 1) 
                : new Vector4(0.3f, 1, 0.3f, 1);
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGui.TextWrapped(statusMessage);
            ImGui.PopStyleColor();
        }
    }

    private async System.Threading.Tasks.Task DoLogin()
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            loginError = true;
            statusMessage = Loc.Text("Error: Please enter email and password");
            return;
        }

        isLoggingIn = true;
        loginError = false;
        statusMessage = Loc.Text("Logging in...");

        try
        {
            var success = await plugin.FFLogsService.LoginAsync(email, password);

            if (success)
            {
                if (rememberMe)
                {
                    plugin.Configuration.Email = email;
                    plugin.Configuration.Password = password; // Transparently encrypted via DPAPI
                    plugin.Configuration.RememberCredentials = true;
                }
                else
                {
                    plugin.Configuration.Email = string.Empty;
                    plugin.Configuration.Password = string.Empty;
                    plugin.Configuration.RememberCredentials = false;
                }
                plugin.Configuration.Save();

                statusMessage = Loc.Text("Login successful!");
                plugin.ShowMainWindow();
            }
            else
            {
                loginError = true;
                statusMessage = Loc.Text("Error: Login failed. Check credentials.");
            }
        }
        catch (Exception ex)
        {
            loginError = true;
            statusMessage = Loc.Text("Could not complete login. See the plugin log for details.");
            Plugin.Log.Error(ex, "Login failed");
        }
        finally
        {
            isLoggingIn = false;
        }
    }
}
