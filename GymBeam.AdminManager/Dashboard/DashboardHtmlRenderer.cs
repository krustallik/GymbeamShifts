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
                :root{color-scheme:dark;--page:#111827;--surface:#1f2937;--surface-2:#0b1220;--border:#374151;--text:#e5e7eb;--muted:#9ca3af;--blue:#2563eb;--blue-hover:#1d4ed8;--green:#34d399;--amber:#fbbf24;--red:#f87171}
                *{box-sizing:border-box}body{font-family:Arial,system-ui,sans-serif;margin:0;background:var(--page);color:var(--text);min-height:100vh}button,input{font:inherit}
                .container{width:min(1440px,100%);margin:auto;padding:24px 20px 48px}.topbar{display:flex;align-items:center;justify-content:space-between;gap:16px;margin-bottom:24px}.eyebrow{margin:0 0 5px;color:#60a5fa;font-size:.75rem;font-weight:700;letter-spacing:.12em;text-transform:uppercase}h1{margin:0;font-size:clamp(1.65rem,4vw,2.35rem)}.subtitle{margin:.45rem 0 0;color:var(--muted)}
                .panel{background:var(--surface);border:1px solid var(--border);border-radius:14px;padding:18px;box-shadow:0 12px 30px #0003}.toolbar{display:flex;align-items:center;justify-content:space-between;gap:12px;margin-bottom:14px}.toolbar h2{margin:0;font-size:1.15rem}.bot-count{padding:.35rem .65rem;border-radius:999px;background:#2563eb24;color:#93c5fd;font-size:.8rem;font-weight:700}
                details{border:1px solid var(--border);border-radius:10px;background:var(--surface-2)}summary{cursor:pointer;padding:13px 15px;font-weight:700;color:#bfdbfe}details[open]>summary{border-bottom:1px solid var(--border)}form{padding:16px}.form-grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:14px}label{display:grid;gap:6px;color:#d1d5db;font-size:.86rem;font-weight:600}input{width:100%;border:1px solid var(--border);border-radius:8px;background:#111827;color:var(--text);padding:10px 11px;outline:none}input:focus{border-color:#60a5fa;box-shadow:0 0 0 3px #2563eb33}
                button{border:1px solid transparent;border-radius:8px;padding:9px 12px;background:var(--blue);color:white;font-weight:700;cursor:pointer;transition:.15s ease}button:hover{background:var(--blue-hover);transform:translateY(-1px)}button:disabled{opacity:.55;cursor:wait;transform:none}.button-secondary{background:#374151}.button-secondary:hover{background:#4b5563}.button-danger{background:#7f1d1d;color:#fecaca}.button-danger:hover{background:#991b1b}.submit-row{display:flex;align-items:center;gap:12px;margin-top:16px}.submit-row button{min-width:150px}.result{color:var(--muted);font-size:.86rem}
                dialog{width:min(520px,calc(100% - 24px));padding:0;border:1px solid var(--border);border-radius:14px;background:var(--surface);color:var(--text);box-shadow:0 24px 80px #000b}dialog::backdrop{background:#020617cc}.message-form{padding:22px}.message-form h2{margin:0}.message-target{margin:6px 0 18px;color:var(--muted)}textarea{width:100%;min-height:150px;resize:vertical;border:1px solid var(--border);border-radius:8px;background:var(--surface-2);color:var(--text);padding:11px;font:inherit;outline:none}textarea:focus{border-color:#60a5fa;box-shadow:0 0 0 3px #2563eb33}.dialog-actions{display:flex;justify-content:flex-end;gap:9px;margin-top:14px}.dialog-actions button{min-width:110px}
                .table-wrap{overflow-x:auto;border:1px solid var(--border);border-radius:10px}table{width:100%;border-collapse:collapse;background:var(--surface-2);min-width:900px}th,td{padding:13px 12px;text-align:left;border-bottom:1px solid #263244;vertical-align:middle}th{background:#172033;color:var(--muted);font-size:.72rem;letter-spacing:.06em;text-transform:uppercase}tbody tr:last-child td{border-bottom:0}tbody tr:hover{background:#162033}.bot-name{font-weight:700}.bot-id{font-family:ui-monospace,monospace;color:var(--muted);font-size:.85rem}.status{display:inline-flex;align-items:center;gap:6px;padding:5px 9px;border-radius:999px;background:#374151;color:#d1d5db;font-size:.78rem;font-weight:700}.status::before{content:'';width:7px;height:7px;border-radius:50%;background:currentColor}.healthy{color:var(--green);background:#064e3b55}.unknown{color:var(--amber);background:#78350f55}.actions{display:flex;flex-wrap:wrap;gap:7px;min-width:310px}.actions button{padding:7px 9px;font-size:.78rem}.operation-result{width:100%;min-height:1rem;color:var(--muted);font-size:.8rem}.bot-logs{width:100%;max-height:280px;overflow:auto;padding:12px;border:1px solid var(--border);border-radius:8px;background:#050a12;color:#cbd5e1;white-space:pre-wrap}.credential-form p{margin-top:0;color:var(--muted);font-size:.85rem}.credential-form{min-width:min(650px,80vw)}
                .empty{padding:28px;text-align:center;color:var(--muted)}@media(max-width:900px){.form-grid{grid-template-columns:1fr 1fr}}@media(max-width:620px){.container{padding:18px 12px 36px}.topbar{align-items:flex-start}.form-grid{grid-template-columns:1fr}.panel{padding:12px}.subtitle{font-size:.9rem}}
              </style>
            </head>
            <body>
              <main class="container">
                <header class="topbar"><div><p class="eyebrow">Control center</p><h1>GymBeam bots</h1><p class="subtitle">Monitor instances, manage access and perform lifecycle operations.</p></div><button type="button" id="logout" class="button-secondary">Log out</button></header>
                <section class="panel">
                <div class="toolbar"><h2>Bot instances</h2><div><span class="bot-count">Managed bots</span> <button type="button" id="message-all">Send message to all</button></div></div>
                <details><summary>+ Provision a new bot</summary>
                  <form class="provision-form" autocomplete="off"><div class="form-grid">
                    <label>Bot ID <input name="botId" required pattern="[a-z][a-z0-9-]{0,63}"></label>
                    <label>Display name <input name="displayName" required maxlength="100"></label>
                    <label>Subdomain <input name="subdomain" required pattern="[a-z][a-z0-9-]{0,63}"></label>
                    <label>Bot admin username <input name="botAdminUsername" required autocomplete="off"></label>
                    <label>Bot admin password <input type="password" name="botAdminPassword" required autocomplete="new-password"></label>
                    <label>Bot admin token <input type="password" name="botAdminToken" required autocomplete="new-password"></label>
                    </div><div class="submit-row"><button type="submit">Provision bot</button>
                    <div class="provision-result result" role="status" aria-live="polite"></div></div>
                  </form>
                </details>
                <div class="table-wrap"><table>
                  <thead><tr><th>Bot</th><th>ID</th><th>State</th><th>Health</th><th>Uptime</th><th>Last update</th><th>Actions</th></tr></thead>
                  <tbody>
            """);

        foreach (BotDashboardItem bot in bots)
        {
            html.Append("<tr><td>").Append(Encode(bot.DisplayName))
                .Append("</td><td class=\"bot-id\">").Append(Encode(bot.Id))
                .Append("</td><td><span class=\"status ").Append(CssClass(bot.State)).Append("\">").Append(Encode(bot.State)).Append("</span>")
                .Append("</td><td><span class=\"status ").Append(CssClass(bot.Health)).Append("\">").Append(Encode(bot.Health)).Append("</span>")
                .Append("</td><td>").Append(Encode(FormatUptime(bot.Uptime)))
                .Append("</td><td>").Append(Encode(FormatTimestamp(bot.LastUpdatedAtUtc)))
                .Append("</td><td data-bot=\"").Append(Encode(bot.Id)).Append("\"><div class=\"actions\">")
                .Append("<button type=\"button\" data-action=\"start\">Start</button> ")
                .Append("<button type=\"button\" data-action=\"stop\">Stop</button> ")
                .Append("<button type=\"button\" data-action=\"restart\">Restart</button> ")
                .Append("<button type=\"button\" data-action=\"enable\">Enable</button> ")
                .Append("<button type=\"button\" data-action=\"disable\">Disable</button> ")
                .Append("<button type=\"button\" class=\"button-danger\" data-action=\"delete\">Delete</button> ")
                .Append("<button type=\"button\" data-action=\"logs\">View logs</button>")
                .Append("<button type=\"button\" data-message-bot=\"").Append(Encode(bot.Id)).Append("\">Send message</button>")
                .Append("<div class=\"operation-result\" role=\"status\" aria-live=\"polite\"></div></div>")
                .Append("<pre class=\"bot-logs\" hidden></pre>")
                .Append("<details><summary>Update credentials</summary>")
                .Append("<form class=\"credential-form\" data-bot=\"").Append(Encode(bot.Id)).Append("\" autocomplete=\"off\"><div class=\"form-grid\">")
                .Append("<p>Blank fields remain unchanged.</p>")
                .Append("<label>Bot admin user <input name=\"botAdminUser\" autocomplete=\"off\"></label>")
                .Append("<label>Bot admin password <input type=\"password\" name=\"botAdminPassword\" autocomplete=\"new-password\"></label>")
                .Append("<label>Bot admin token secret <input type=\"password\" name=\"botAdminTokenSecret\" autocomplete=\"new-password\"></label>")
                .Append("</div><div class=\"submit-row\"><button type=\"submit\">Save credentials</button>")
                .Append("<div class=\"credential-result result\" role=\"status\" aria-live=\"polite\"></div></div>")
                .Append("</form></details>")
                .Append("</td></tr>");
        }

        html.Append(
            """
            </tbody></table></div></section></main>
            <dialog id="message-dialog"><form class="message-form" id="message-form"><h2>Send Telegram message</h2><p class="message-target" id="message-target"></p><label>Message<textarea id="message-text" maxlength="4096" required placeholder="Enter the message to send..."></textarea></label><div class="dialog-actions"><button type="button" class="button-secondary" id="message-cancel">Cancel</button><button type="submit">Send</button></div><div class="result" id="message-result" role="status" aria-live="polite"></div></form></dialog>
            <script>
            document.getElementById('logout').addEventListener('click',async()=>{
              const csrfResponse=await fetch('/api/auth/csrf',{credentials:'same-origin'});
              const csrf=await csrfResponse.json();
              await fetch('/api/auth/logout',{method:'POST',credentials:'same-origin',headers:{'X-CSRF-Token':csrf.csrfToken}});
              location.replace('/login');
            });
            const messageDialog=document.getElementById('message-dialog');
            const messageForm=document.getElementById('message-form');
            const messageText=document.getElementById('message-text');
            const messageResult=document.getElementById('message-result');
            let messageBotId=null;
            function openMessageDialog(botId){
              messageBotId=botId;messageForm.reset();messageResult.textContent='';
              document.getElementById('message-target').textContent=botId?'Recipient: '+botId:'Recipients: all active bots';
              messageDialog.showModal();messageText.focus();
            }
            document.getElementById('message-all').addEventListener('click',()=>openMessageDialog(null));
            document.getElementById('message-cancel').addEventListener('click',()=>messageDialog.close());
            document.addEventListener('click',event=>{
              const button=event.target.closest('button[data-message-bot]');
              if(button)openMessageDialog(button.dataset.messageBot);
            });
            messageForm.addEventListener('submit',async event=>{
              event.preventDefault();
              const button=messageForm.querySelector('button[type="submit"]');
              button.disabled=true;messageResult.textContent='Sending...';
              try{
                const csrfResponse=await fetch('/api/auth/csrf',{credentials:'same-origin'});
                const csrf=await csrfResponse.json();
                const path=messageBotId?'/api/bots/'+encodeURIComponent(messageBotId)+'/telegram-message':'/api/bots/telegram-message-all';
                const response=await fetch(path,{method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json','X-CSRF-Token':csrf.csrfToken},body:JSON.stringify({message:messageText.value})});
                const data=await response.json();
                if(!response.ok)throw new Error(data.message||data.outcome||'Message failed');
                messageResult.textContent=messageBotId?'Message sent':'Sent: '+data.sent+', failed: '+data.failed;
                if(response.ok)setTimeout(()=>messageDialog.close(),900);
              }catch(error){messageResult.textContent=error.message||'Message failed';}
              finally{button.disabled=false;}
            });
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
