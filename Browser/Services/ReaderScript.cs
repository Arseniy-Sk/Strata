namespace Browser.Services;

/// <summary>Reader mode: locate the main text block and rebuild the page in Strata's calm typography.</summary>
public static class ReaderScript
{
    public static string Build(bool dark, string accent) => Template
        .Replace("__BG__", dark ? "#0B0D10" : "#FBFAF8")
        .Replace("__TEXT__", dark ? "#E7EBF0" : "#15181D")
        .Replace("__READ__", dark ? "#D7DDE5" : "#23272E")
        .Replace("__DIM__", dark ? "#96A0AD" : "#5B6570")
        .Replace("__HAIR__", dark ? "rgba(255,255,255,.08)" : "rgba(12,16,22,.1)")
        .Replace("__ACCENT__", accent);

    private const string Template = """
    (() => {
      const pick = () => {
        const direct = document.querySelector('article, [role="main"] article, main article, .post-content, .article-content, .entry-content');
        if (direct && direct.innerText.length > 600) return direct;
        let best = null, bestScore = 0;
        document.querySelectorAll('article, main, section, div').forEach(el => {
          const ps = el.querySelectorAll(':scope > p, :scope > div > p');
          if (ps.length < 3) return;
          let text = 0; ps.forEach(p => text += p.innerText.length);
          const links = [...el.querySelectorAll('a')].reduce((n, a) => n + a.innerText.length, 0);
          const score = text - links * 1.5;
          if (score > bestScore) { bestScore = score; best = el; }
        });
        return best;
      };
      const root = pick();
      if (!root) return false;
      const allowed = new Set(['P','H1','H2','H3','H4','UL','OL','LI','BLOCKQUOTE','PRE','CODE','FIGURE','IMG','FIGCAPTION','EM','STRONG','A','TABLE','THEAD','TBODY','TR','TD','TH','BR','HR']);
      const clean = (node) => {
        if (node.nodeType === 3) return document.createTextNode(node.textContent);
        if (node.nodeType !== 1) return null;
        const tag = node.tagName;
        if (['SCRIPT','STYLE','NAV','ASIDE','FORM','BUTTON','IFRAME','SVG','NOSCRIPT','FOOTER','HEADER'].includes(tag)) return null;
        const out = allowed.has(tag) ? document.createElement(tag) : document.createDocumentFragment();
        if (tag === 'IMG') { const src = node.currentSrc || node.src; if (!src) return null; out.src = src; out.alt = node.alt || ''; return out; }
        if (tag === 'A' && node.href) out.href = node.href;
        node.childNodes.forEach(c => { const r = clean(c); if (r) out.appendChild(r); });
        return out;
      };
      const title = (document.querySelector('h1') || {}).innerText || document.title;
      const body = clean(root);
      const words = (root.innerText || '').split(/\s+/).length;
      const minutes = Math.max(1, Math.round(words / 200));
      const host = location.hostname.replace(/^www\./, '');
      document.head.innerHTML = '<meta charset="utf-8"><title>' + document.title.replace(/</g,'&lt;') + '</title>';
      const style = document.createElement('style');
      style.textContent = `
        html,body{background:__BG__;margin:0}
        body{font:17px/1.72 'Segoe UI Variable Text','Segoe UI',system-ui,sans-serif;color:__READ__;-webkit-font-smoothing:antialiased}
        .strata-reader{max-width:680px;margin:0 auto;padding:56px 32px 96px}
        .strata-meta{font:11px/1 'Cascadia Mono',Consolas,monospace;letter-spacing:.14em;color:__DIM__;text-transform:uppercase}
        .strata-title{font-size:38px;line-height:1.14;font-weight:600;letter-spacing:-.02em;color:__TEXT__;margin:16px 0 8px}
        .strata-host{font-size:13px;color:__DIM__;margin-bottom:32px;padding-bottom:24px;border-bottom:1px solid __HAIR__}
        .strata-body h1{display:none} .strata-body h2,.strata-body h3{color:__TEXT__;line-height:1.3;margin:1.8em 0 .6em}
        .strata-body p{margin:0 0 1.1em} .strata-body a{color:__ACCENT__;text-decoration:none;border-bottom:1px solid __HAIR__}
        .strata-body img{max-width:100%;height:auto;border-radius:12px;margin:12px 0}
        .strata-body blockquote{margin:1.4em 0;padding-left:18px;border-left:2px solid __ACCENT__;color:__TEXT__;font-size:19px;line-height:1.55}
        .strata-body pre{background:__HAIR__;padding:14px 16px;border-radius:10px;overflow:auto;font:13px/1.55 'Cascadia Mono',Consolas,monospace}
        .strata-body code{font-family:'Cascadia Mono',Consolas,monospace;font-size:.9em}
        .strata-body figcaption{font-size:13px;color:__DIM__}
        .strata-body table{border-collapse:collapse;width:100%} .strata-body td,.strata-body th{border-bottom:1px solid __HAIR__;padding:8px;text-align:left}
      `;
      document.head.appendChild(style);
      const wrap = document.createElement('div');
      wrap.className = 'strata-reader';
      wrap.innerHTML = '<div class="strata-meta">Reader mode · ' + minutes + ' min</div><div class="strata-title"></div><div class="strata-host">' + host + '</div><div class="strata-body"></div>';
      wrap.querySelector('.strata-title').textContent = title;
      wrap.querySelector('.strata-body').appendChild(body);
      document.body.replaceWith(document.createElement('body'));
      document.body.appendChild(wrap);
      window.scrollTo(0, 0);
      return true;
    })();
    """;
}
