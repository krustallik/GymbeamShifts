using System.Text;
using System.Text.Encodings.Web;

namespace GymBeam.AdminManager.Dashboard;

public static class DashboardHtmlRenderer
{
    public static string Render(IReadOnlyList<BotDashboardItem> bots)
    {
        var html = new StringBuilder(
            """
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>GymBeam Admin Manager</title>
              <style>
                body{font-family:system-ui,sans-serif;margin:2rem;background:#f6f7f9;color:#172033}
                table{width:100%;border-collapse:collapse;background:white}
                th,td{padding:.75rem;text-align:left;border-bottom:1px solid #dfe3e8}
                th{background:#eef1f5}.unknown{color:#6b7280}.healthy{color:#087f5b}
              </style>
            </head>
            <body>
              <main>
                <h1>GymBeam bots</h1>
                <details><summary>Provision new bot</summary>
                  <form class="provision-form" autocomplete="off">
                    <label>Bot ID <input name="botId" required pattern="[a-z][a-z0-9-]{0,63}"></label>
                    <label>Display name <input name="displayName" required maxlength="100"></label>
                    <label>Subdomain <input name="subdomain" required pattern="[a-z][a-z0-9-]{0,63}"></label>
                    <label>GymBeam login <input name="gymBeamLogin" required autocomplete="off"></label>
                    <label>GymBeam password <input type="password" name="gymBeamPassword" required autocomplete="new-password"></label>
                    <label>Telegram bot token <input type="password" name="telegramToken" required autocomplete="new-password"></label>
                    <label>Telegram chat ID <input name="telegramChatId" required autocomplete="off"></label>
                    <label>Bot admin username <input name="botAdminUsername" required autocomplete="off"></label>
                    <label>Bot admin password <input type="password" name="botAdminPassword" required autocomplete="new-password"></label>
                    <label>Bot admin token <input type="password" name="botAdminToken" required autocomplete="new-password"></label>
                    <button type="submit">Provision bot</button>
                    <div class="provision-result" role="status" aria-live="polite"></div>
                  </form>
                </details>
                <table>
                  <thead><tr><th>Bot</th><th>ID</th><th>State</th><th>Health</th><th>Uptime</th><th>Last update</th><th>Actions</th></tr></thead>
                  <tbody>
            """);

        foreach (BotDashboardItem bot in bots)
        {
            html.Append("<tr><td>").Append(Encode(bot.DisplayName))
                .Append("</td><td>").Append(Encode(bot.Id))
                .Append("</td><td class=\"").Append(CssClass(bot.State)).Append("\">").Append(Encode(bot.State))
                .Append("</td><td class=\"").Append(CssClass(bot.Health)).Append("\">").Append(Encode(bot.Health))
                .Append("</td><td>").Append(Encode(FormatUptime(bot.Uptime)))
                .Append("</td><td>").Append(Encode(FormatTimestamp(bot.LastUpdatedAtUtc)))
                .Append("</td><td data-bot=\"").Append(Encode(bot.Id)).Append("\">")
                .Append("<button type=\"button\" data-action=\"start\">Start</button> ")
                .Append("<button type=\"button\" data-action=\"stop\">Stop</button> ")
                .Append("<button type=\"button\" data-action=\"restart\">Restart</button> ")
                .Append("<button type=\"button\" data-action=\"enable\">Enable</button> ")
                .Append("<button type=\"button\" data-action=\"disable\">Disable</button> ")
                .Append("<button type=\"button\" data-action=\"delete\">Delete</button> ")
                .Append("<button type=\"button\" data-action=\"logs\">View logs</button>")
                .Append("<div class=\"operation-result\" role=\"status\" aria-live=\"polite\"></div>")
                .Append("<pre class=\"bot-logs\" hidden></pre>")
                .Append("<details><summary>Update credentials</summary>")
                .Append("<form class=\"credential-form\" data-bot=\"").Append(Encode(bot.Id)).Append("\" autocomplete=\"off\">")
                .Append("<p>Blank fields remain unchanged.</p>")
                .Append("<label>GymBeam login <input name=\"gymBeamLogin\" autocomplete=\"off\"></label>")
                .Append("<label>GymBeam password <input type=\"password\" name=\"gymBeamPassword\" autocomplete=\"new-password\"></label>")
                .Append("<label>Telegram bot token <input type=\"password\" name=\"telegramBotToken\" autocomplete=\"new-password\"></label>")
                .Append("<label>Telegram chat ID <input name=\"telegramChatId\" autocomplete=\"off\"></label>")
                .Append("<label>Bot admin user <input name=\"botAdminUser\" autocomplete=\"off\"></label>")
                .Append("<label>Bot admin password <input type=\"password\" name=\"botAdminPassword\" autocomplete=\"new-password\"></label>")
                .Append("<label>Bot admin token secret <input type=\"password\" name=\"botAdminTokenSecret\" autocomplete=\"new-password\"></label>")
                .Append("<button type=\"submit\">Save credentials</button>")
                .Append("<div class=\"credential-result\" role=\"status\" aria-live=\"polite\"></div>")
                .Append("</form></details>")
                .Append("</td></tr>");
        }

        html.Append(
            """
            </tbody></table></main>
            <script>
            document.addEventListener('click',async event=>{
              const button=event.target.closest('button[data-action]');
              if(!button)return;
              const cell=button.closest('[data-bot]');
              const result=cell.querySelector('.operation-result');
              const logs=cell.querySelector('.bot-logs');
              const buttons=cell.querySelectorAll('button[data-action]');
              buttons.forEach(item=>item.disabled=true);
              result.textContent=button.dataset.action==='logs'?'Loading logs…':'Operation in progress…';
              try{
                let response;
                if(button.dataset.action==='logs'){
                  response=await fetch('/api/bots/'+encodeURIComponent(cell.dataset.bot)+'/logs?tail=200',{credentials:'same-origin'});
                }else{
                  const csrfResponse=await fetch('/api/auth/csrf',{credentials:'same-origin'});
                  const csrf=await csrfResponse.json();
                  const isDelete=button.dataset.action==='delete';
                  let confirmation;
                  if(isDelete){
                    confirmation=prompt('Type DELETE '+cell.dataset.bot+' to confirm recoverable deletion');
                    if(confirmation===null){result.textContent='Delete cancelled';return;}
                  }
                  response=await fetch('/api/bots/'+encodeURIComponent(cell.dataset.bot)+'/'+button.dataset.action,{
                    method:'POST',credentials:'same-origin',
                    headers:isDelete?{'Content-Type':'application/json','X-CSRF-Token':csrf.csrfToken}:{'X-CSRF-Token':csrf.csrfToken},
                    body:isDelete?JSON.stringify({confirmation}):undefined
                  });
                }
                const data=await response.json();
                if(button.dataset.action==='logs'&&response.ok){logs.textContent=data.logs;logs.hidden=false;}
                result.textContent=data.message||data.outcome||(response.ok?'Completed':'Operation failed');
              }catch(error){result.textContent='Operation failed';}
              finally{buttons.forEach(item=>item.disabled=false);}
            });
            document.addEventListener('submit',async event=>{
              const form=event.target.closest('form.credential-form');
              if(!form)return;
              event.preventDefault();
              const button=form.querySelector('button[type="submit"]');
              const result=form.querySelector('.credential-result');
              button.disabled=true;
              result.textContent='Credential update in progress…';
              try{
                const csrfResponse=await fetch('/api/auth/csrf',{credentials:'same-origin'});
                const csrf=await csrfResponse.json();
                const payload=Object.fromEntries(new FormData(form).entries());
                const response=await fetch('/api/bots/'+encodeURIComponent(form.dataset.bot)+'/credentials',{
                  method:'PUT',credentials:'same-origin',
                  headers:{'Content-Type':'application/json','X-CSRF-Token':csrf.csrfToken},
                  body:JSON.stringify(payload)
                });
                const data=await response.json();
                result.textContent=data.message||data.outcome||'Credential update failed';
              }catch(error){result.textContent='Credential update failed';}
              finally{form.reset();button.disabled=false;}
            });
            document.addEventListener('submit',async event=>{
              const form=event.target.closest('form.provision-form');
              if(!form)return;
              event.preventDefault();
              const button=form.querySelector('button[type="submit"]');
              const result=form.querySelector('.provision-result');
              button.disabled=true;
              result.textContent='Provisioning in progress…';
              try{
                const csrfResponse=await fetch('/api/auth/csrf',{credentials:'same-origin'});
                const csrf=await csrfResponse.json();
                const payload=Object.fromEntries(new FormData(form).entries());
                const response=await fetch('/api/bots/provision',{
                  method:'POST',credentials:'same-origin',
                  headers:{'Content-Type':'application/json','X-CSRF-Token':csrf.csrfToken},
                  body:JSON.stringify(payload)
                });
                const data=await response.json();
                result.textContent=data.outcome||(response.ok?'Completed':'Provisioning failed');
                if(response.ok&&data.outcome==='succeeded'){form.reset();location.reload();}
              }catch(error){result.textContent='Provisioning failed';}
              finally{button.disabled=false;}
            });
            </script></body></html>
            """);
        return html.ToString();
    }

    private static string Encode(string value) => HtmlEncoder.Default.Encode(value);

    private static string CssClass(string value)
    {
        return value is "healthy" or "running" ? "healthy" : "unknown";
    }

    private static string FormatUptime(TimeSpan? uptime)
    {
        if (uptime is null)
        {
            return "unknown";
        }

        TimeSpan value = uptime.Value < TimeSpan.Zero ? TimeSpan.Zero : uptime.Value;
        return value.Days > 0
            ? $"{value.Days}d {value.Hours}h {value.Minutes}m"
            : $"{value.Hours}h {value.Minutes}m";
    }

    private static string FormatTimestamp(DateTimeOffset? value)
    {
        return value?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "unknown";
    }
}
