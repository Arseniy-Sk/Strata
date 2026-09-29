using System.Net.Http;
using System.Text.Json;

namespace Browser.Services;

/// <summary>
/// Перевод текста через открытый endpoint Google. Сетевые запросы идут из C# (HttpClient),
/// поэтому CSP страницы их не блокирует — перевод работает на любом сайте.
/// </summary>
public static class TranslationService
{
    /// <summary>Языки для плавающей панели переводчика: код Google Translate + человекочитаемое имя.</summary>
    public static readonly (string Code, string Name)[] Languages =
    {
        ("ru", "Русский"), ("en", "English"), ("es", "Español"), ("de", "Deutsch"),
        ("fr", "Français"), ("it", "Italiano"), ("pt", "Português"), ("tr", "Türkçe"),
        ("zh-CN", "中文"), ("ja", "日本語"), ("ko", "한국어"), ("ar", "العربية"), ("uk", "Українська"),
    };

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(12),
        DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0" } }
    };

    public static async Task<string> TranslateAsync(string text, string target, string source = "auto")
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={source}&tl={target}&dt=t&q={Uri.EscapeDataString(text)}";
        var json = await Http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        var sb = new System.Text.StringBuilder();
        foreach (var seg in doc.RootElement[0].EnumerateArray())
            if (seg[0].ValueKind == JsonValueKind.String) sb.Append(seg[0].GetString());
        return sb.Length > 0 ? sb.ToString() : text;
    }

    /// <summary>Скрипт: собирает текстовые узлы страницы, шлёт их хосту порциями и заменяет переводом.</summary>
    public const string PageCollectScript = """
    (() => {
      if (window.__strataTr) { window.__strataTr.restore(); if (window.__strataTr.on) { window.__strataTr = null; return 'off'; } }
      const skip = new Set(['SCRIPT','STYLE','NOSCRIPT','CODE','PRE','TEXTAREA','KBD','SAMP']);
      const nodes = [], original = [];
      const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
        acceptNode(n){ if(!n.nodeValue||!n.nodeValue.trim()||n.nodeValue.trim().length<2) return 2;
          let p=n.parentElement; if(!p||skip.has(p.tagName)||p.isContentEditable) return 2;
          const r=p.getBoundingClientRect(); if(r.width===0&&r.height===0) return 2; return 1; }});
      let n; while((n=walker.nextNode())){ nodes.push(n); original.push(n.nodeValue); }
      window.__strataTr = { nodes, original, on:true, restore(){ this.nodes.forEach((x,i)=>{ if(x) x.nodeValue=this.original[i]; }); this.on=false; } };
      const texts = nodes.map(x=>x.nodeValue);
      const CHUNK=60;
      for(let i=0;i<texts.length;i+=CHUNK)
        chrome.webview.postMessage(JSON.stringify({type:'tr-page',start:i,texts:texts.slice(i,i+CHUNK)}));
      return 'on:'+texts.length;
    })();
    """;

    public static string ApplyPageScript(int start, string[] translations)
    {
        var json = JsonSerializer.Serialize(translations);
        return $"(()=>{{const t={json},s={start};if(!window.__strataTr)return;for(let i=0;i<t.length;i++){{const n=window.__strataTr.nodes[s+i];if(n&&t[i])n.nodeValue=t[i];}}}})();";
    }

    public const string QuickListenScript = """
    (() => {
      if (window.__strataQuick) return;
      window.__strataQuick = true;
      let bubble;
      const kill = () => { if (bubble) { bubble.remove(); bubble = null; } };
      document.addEventListener('mousedown', e => { if (bubble && !bubble.contains(e.target)) kill(); }, true);
      document.addEventListener('mouseup', () => {
        setTimeout(() => {
          const sel = (window.getSelection().toString() || '').trim();
          if (sel.length < 2 || sel.length > 800) return;
          chrome.webview.postMessage(JSON.stringify({ type: 'tr-quick', text: sel }));
        }, 10);
      });
      window.__strataShowBubble = (text) => {
        kill();
        const sel = window.getSelection();
        if (!sel.rangeCount) return;
        const r = sel.getRangeAt(0).getBoundingClientRect();
        bubble = document.createElement('div');
        bubble.textContent = text;
        bubble.style.cssText = 'position:fixed;z-index:2147483647;max-width:360px;font:14px/1.5 system-ui,sans-serif;' +
          'color:#E7EBF0;background:rgba(20,23,29,.97);border:1px solid rgba(255,255,255,.16);border-radius:12px;' +
          'padding:10px 13px;box-shadow:0 18px 50px -12px rgba(0,0,0,.7);backdrop-filter:blur(12px);white-space:pre-wrap';
        document.body.appendChild(bubble);
        const top = r.bottom + 8, left = Math.max(8, Math.min(r.left, innerWidth - bubble.offsetWidth - 8));
        bubble.style.top = (top + bubble.offsetHeight > innerHeight ? r.top - bubble.offsetHeight - 8 : top) + 'px';
        bubble.style.left = left + 'px';
      };
    })();
    """;

    public static string ShowBubbleScript(string text)
        => $"window.__strataShowBubble && window.__strataShowBubble({JsonSerializer.Serialize(text)});";

    /// <summary>Вынести первое (или воспроизводящееся) видео в отдельное окно поверх других.</summary>
    public const string PictureInPictureScript = """
    (async () => {
      const vids = [...document.querySelectorAll('video')].filter(v => v.readyState > 0);
      const v = vids.find(x => !x.paused) || vids.sort((a,b)=>(b.clientWidth*b.clientHeight)-(a.clientWidth*a.clientHeight))[0];
      if (!v) return 'novideo';
      try { if (document.pictureInPictureElement) { await document.exitPictureInPicture(); return 'exit'; }
            await v.requestPictureInPicture(); return 'ok'; }
      catch (e) { return 'fail:' + e.message; }
    })();
    """;

    /// <summary>
    /// Постоянный content-script: показывает при наведении на видео маленькую кнопку «вынести в
    /// отдельное окно» — как в Firefox/Яндекс.Браузере. YouTube и большинство плееров сами не рисуют
    /// такую кнопку в WebView2, поэтому рисуем её сами поверх видео.
    ///
    /// Наведение отслеживается ГЕОМЕТРИЧЕСКИ (координаты курсора против getBoundingClientRect видео),
    /// а не через mouseenter/mousemove НА САМОМ &lt;video&gt; — у YouTube и большинства плееров поверх
    /// видео лежат свои элементы управления, которые перехватывают события мыши, и слушатели на самом
    /// теге video из-за этого почти никогда не срабатывают.
    /// </summary>
    public const string HoverPipScript = """
    (() => {
      if (window.__strataPip) return;
      window.__strataPip = true;
      if (!('pictureInPictureEnabled' in document) || !document.pictureInPictureEnabled) return;

      const entries = new Map(); // video -> { btn, visible }

      // Иконка рисуется через createElement/style, а не innerHTML: у YouTube и части других сайтов
      // включён Trusted Types CSP, который блокирует любое присваивание innerHTML простой строкой.
      function makeIcon() {
        const icon = document.createElement('span');
        icon.style.cssText = 'position:relative;display:block;width:17px;height:13px;' +
          'border:1.6px solid #fff;border-radius:2px;box-sizing:border-box';
        const inner = document.createElement('span');
        inner.style.cssText = 'position:absolute;right:-1.6px;bottom:-1.6px;width:8px;height:6px;' +
          'background:#fff;border-radius:1px';
        icon.appendChild(inner);
        return icon;
      }

      function makeButton(video) {
        const btn = document.createElement('div');
        btn.appendChild(makeIcon());
        btn.style.cssText = 'position:fixed;z-index:2147483000;width:32px;height:32px;border-radius:8px;' +
          'background:rgba(15,17,21,.7);backdrop-filter:blur(6px);display:flex;align-items:center;justify-content:center;' +
          'cursor:pointer;opacity:0;pointer-events:none;transition:opacity .12s ease,background .12s ease';
        btn.addEventListener('mouseenter', () => btn.style.background = 'rgba(31,181,188,.85)');
        btn.addEventListener('mouseleave', () => btn.style.background = 'rgba(15,17,21,.7)');
        btn.addEventListener('mousedown', e => { e.preventDefault(); e.stopPropagation(); });
        btn.addEventListener('click', e => {
          e.preventDefault(); e.stopPropagation();
          if (document.pictureInPictureElement === video) document.exitPictureInPicture().catch(() => {});
          else video.requestPictureInPicture().catch(() => {});
        });
        document.documentElement.appendChild(btn);
        return btn;
      }

      function setVisible(entry, visible, rect) {
        if (visible === entry.visible && !rect) return;
        entry.visible = visible;
        entry.btn.style.opacity = visible ? '1' : '0';
        entry.btn.style.pointerEvents = visible ? 'auto' : 'none';
        if (visible && rect) {
          entry.btn.style.left = Math.round(rect.right - 42) + 'px';
          entry.btn.style.top = Math.round(rect.top + 10) + 'px';
        }
      }

      function track(video) {
        if (entries.has(video) || video.disablePictureInPicture) return;
        entries.set(video, { btn: makeButton(video), visible: false });
        video.addEventListener('emptied', () => {
          const e = entries.get(video);
          if (e) { e.btn.remove(); entries.delete(video); }
        });
      }

      let lastX = -1, lastY = -1;
      function evaluate() {
        if (lastX < 0) return;
        for (const [video, entry] of entries) {
          const r = video.getBoundingClientRect();
          const bigEnough = r.width >= 140 && r.height >= 90;
          const onScreen = r.bottom > 0 && r.top < innerHeight && r.right > 0 && r.left < innerWidth;
          const inside = lastX >= r.left && lastX <= r.right && lastY >= r.top && lastY <= r.bottom;
          setVisible(entry, bigEnough && onScreen && inside, r);
        }
      }

      document.addEventListener('mousemove', e => { lastX = e.clientX; lastY = e.clientY; evaluate(); }, true);
      window.addEventListener('scroll', evaluate, true);
      window.addEventListener('resize', evaluate);
      setInterval(evaluate, 500); // ловим случаи, когда видео появилось/изменилось без движения мыши

      function scan(root) {
        (root.querySelectorAll ? root.querySelectorAll('video') : []).forEach(track);
      }

      // AddScriptToExecuteOnDocumentCreatedAsync выполняет скрипт ДО того, как в документе появится
      // <html> — document.documentElement в этот момент равен null, и observe() на нём падает
      // с исключением, из-за чего ни один тег video так и не начинает отслеживаться. Ждём его появления.
      function start() {
        if (!document.documentElement) { requestAnimationFrame(start); return; }
        scan(document);
        new MutationObserver(muts => {
          for (const m of muts) for (const n of m.addedNodes) if (n.nodeType === 1) { if (n.tagName === 'VIDEO') track(n); else scan(n); }
        }).observe(document.documentElement, { childList: true, subtree: true });
      }
      start();
    })();
    """;
}
