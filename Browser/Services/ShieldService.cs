using Browser.Models;
using Microsoft.Web.WebView2.Core;

namespace Browser.Services;

/// <summary>
/// Щит на сетевом уровне. Фильтры регистрируются в движке по доменам, поэтому
/// обычные запросы вообще не доходят до .NET — в обработчик попадают только кандидаты на блокировку.
/// Дополняется косметической фильтрацией (CosmeticCss) и подавлением рекламы в плеере YouTube
/// (YoutubeAdSkipScript) — сетевой блокировкой её не остановить, она отдаётся с того же CDN, что и видео.
/// </summary>
public static class ShieldService
{
    private static readonly string[] Domains =
    {
        // Рекламные сети
        "doubleclick.net", "googlesyndication.com", "googleadservices.com", "adservice.google.com",
        "googletagservices.com", "pagead2.googlesyndication.com", "adnxs.com", "adsrvr.org", "criteo.com",
        "criteo.net", "taboola.com", "outbrain.com", "rubiconproject.com", "pubmatic.com", "openx.net",
        "casalemedia.com", "advertising.com", "adform.net", "smartadserver.com", "moatads.com",
        "amazon-adsystem.com", "media.net", "yieldmo.com", "sharethrough.com", "teads.tv", "33across.com",
        "bidswitch.net", "contextweb.com", "indexww.com", "lijit.com", "sovrn.com", "spotxchange.com",
        "zedo.com", "revcontent.com", "mgid.com", "propellerads.com", "popads.net", "adcash.com",
        "exoclick.com", "trafficjunky.net", "adroll.com", "adtech.de", "serving-sys.com", "undertone.com",
        "adsafeprotected.com", "doubleverify.com", "admixer.net", "betweendigital.com", "adriver.ru",
        "adfox.ru", "an.yandex.ru", "yandexadexchange.net", "buzzoola.com", "otm-r.com", "adhigh.net",
        "imasdk.googleapis.com", "video-ad-stats.googlesyndication.com", "static.criteo.net", "gum.criteo.com",
        "cas.criteo.com", "flashtalking.com", "yieldlab.net", "adition.com", "improvedigital.com",
        "smaato.net", "inmobi.com", "startapp.com", "unityads.unity3d.com", "vungle.com", "applovin.com",
        "chartboost.com", "ironsrc.com", "supersonicads.com", "aditic.ru", "begun.ru", "kavanga.ru", "adx1.com",
        "adservice.google.ru", "adservice.google.de", "adservice.google.fr", "adservice.google.co.uk",
        "adservice.google.es", "adservice.google.it", "adservice.google.pl", "adservice.google.co.jp",
        "adservice.google.com.br", "adservice.google.co.in",
        // Аналитика и трекеры
        "google-analytics.com", "googletagmanager.com", "analytics.google.com", "hotjar.com", "hotjar.io",
        "mouseflow.com", "fullstory.com", "crazyegg.com", "clarity.ms", "quantserve.com", "scorecardresearch.com",
        "comscore.com", "chartbeat.com", "chartbeat.net", "newrelic.com", "nr-data.net", "segment.io",
        "segment.com", "mixpanel.com", "amplitude.com", "heapanalytics.com", "kissmetrics.com", "optimizely.com",
        "branch.io", "appsflyer.com", "adjust.com", "kochava.com", "bluekai.com", "krxd.net", "exelator.com",
        "demdex.net", "omtrdc.net", "everesttech.net", "rlcdn.com", "agkn.com", "bounceexchange.com",
        "addthis.com", "sharethis.com", "tapad.com", "mathtag.com", "turn.com", "eyeota.net", "liadm.com",
        "id5-sync.com", "cxense.com", "top-fwz1.mail.ru", "top.mail.ru", "mc.yandex.ru", "counter.yadro.ru",
        "tns-counter.ru", "mediametrics.ru", "facebook.net", "connect.facebook.net", "pixel.facebook.com",
        "ads-twitter.com", "static.ads-twitter.com", "analytics.twitter.com", "ads.linkedin.com",
        "px.ads.linkedin.com", "snap.licdn.com", "analytics.tiktok.com", "ads.tiktok.com", "bat.bing.com",
        "ct.pinterest.com", "sc-static.net", "tr.snapchat.com", "hs-analytics.net", "hs-banner.com",
        "matomo.cloud", "statcounter.com", "histats.com", "clicky.com", "woopra.com", "luckyorange.com",
        "inspectlet.com", "smartlook.com", "cdn.mxpnl.com", "yastatic.net"
    };

    private static readonly HashSet<string> HostSet = new(Domains, StringComparer.OrdinalIgnoreCase);

    /// <summary>Пути телеметрии/рекламы на СВОИХ (first-party) хостах — точечно, чтобы не сломать сайт целиком.</summary>
    private static readonly string[] BlockedPathFragments =
    {
        "/pagead/", "/api/stats/ads", "/api/stats/qoe?", "/ptracking?",
    };

    public static bool Enabled(BrowserSettings settings) => settings.Shield != ShieldLevel.Off;

    public static void Attach(CoreWebView2 core, BrowserSettings settings)
    {
        if (!Enabled(settings)) return;
        foreach (var domain in HostSet)
            core.AddWebResourceRequestedFilter($"*://*{domain}/*", CoreWebView2WebResourceContext.All);
    }

    public static void Detach(CoreWebView2 core)
    {
        foreach (var domain in HostSet)
        {
            try { core.RemoveWebResourceRequestedFilter($"*://*{domain}/*", CoreWebView2WebResourceContext.All); }
            catch { /* фильтр мог быть не зарегистрирован */ }
        }
    }

    /// <summary>Шаблон фильтра грубый (подстрока), поэтому здесь точная проверка по суффиксу домена и пути.</summary>
    public static bool IsBlocked(string requestUrl, string pageHost)
    {
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host;

        var match = MatchDomain(host);
        if (match != null)
        {
            // Не ломаем сайт, который пользователь открыл сам (first-party), кроме известных путей телеметрии.
            bool firstParty = pageHost.Equals(match, StringComparison.OrdinalIgnoreCase)
                               || pageHost.EndsWith("." + match, StringComparison.OrdinalIgnoreCase);
            if (!firstParty) return true;
        }

        var pathAndQuery = uri.PathAndQuery;
        foreach (var fragment in BlockedPathFragments)
            if (pathAndQuery.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static string? MatchDomain(string host)
    {
        var span = host;
        while (true)
        {
            if (HostSet.Contains(span)) return span;
            int dot = span.IndexOf('.');
            if (dot < 0 || dot == span.Length - 1) return null;
            span = span[(dot + 1)..];
        }
    }

    public static CoreWebView2TrackingPreventionLevel TrackingLevel(ShieldLevel level) => level switch
    {
        ShieldLevel.Strict => CoreWebView2TrackingPreventionLevel.Strict,
        ShieldLevel.Balanced => CoreWebView2TrackingPreventionLevel.Balanced,
        ShieldLevel.Basic => CoreWebView2TrackingPreventionLevel.Basic,
        _ => CoreWebView2TrackingPreventionLevel.None
    };

    /// <summary>
    /// Косметическая фильтрация: прячет типичные рекламные контейнеры и виджеты YouTube,
    /// которые остаются пустыми блоками после блокировки сетевых запросов к рекламным сетям.
    /// </summary>
    public const string CosmeticCss = """
    (() => {
      if (document.getElementById('strata-adblock-css')) return;
      var sel = [
        'ins.adsbygoogle', '.adsbygoogle', '[id^="google_ads_iframe"]', '[id^="div-gpt-ad"]',
        '[class*="ad-banner" i]', '[class*="ad-container" i]', '[class*="ad-slot" i]', '[id*="ad-slot" i]',
        '[class*="sponsored-content" i]', '[data-ad-slot]', '[data-native-ad]', 'iframe[id^="aswift_"]',
        'ytd-display-ad-renderer', 'ytd-promoted-sparkles-web-renderer', 'ytd-promoted-video-renderer',
        'ytd-ad-slot-renderer', 'ytd-in-feed-ad-layout-renderer', 'ytd-banner-promo-renderer',
        'ytd-statement-banner-renderer', '#player-ads', '.ytp-ad-overlay-container', '.ytp-ad-text-overlay',
        '.ytp-ad-image-overlay', '.video-ads.ytp-ad-module'
      ].join(',');
      var css = sel + '{display:none!important;visibility:hidden!important;min-height:0!important;height:0!important}';
      function inject() {
        if (document.getElementById('strata-adblock-css') || !document.head) return;
        var style = document.createElement('style');
        style.id = 'strata-adblock-css';
        style.textContent = css;
        document.head.appendChild(style);
      }
      if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', inject);
      else inject();
      // Скрипт стартует раньше, чем в документе появляется <html> — ждём его, иначе observe() падает
      // с исключением ещё до регистрации наблюдателя.
      (function watch() {
        if (!document.documentElement) { requestAnimationFrame(watch); return; }
        new MutationObserver(inject).observe(document.documentElement, { childList: true });
      })();
    })();
    """;

    /// <summary>
    /// Реклама в плеере YouTube отдаётся с того же CDN, что и обычное видео, поэтому её нельзя
    /// заблокировать на уровне сети, не сломав воспроизведение. Вместо этого сразу жмём «Пропустить»,
    /// когда кнопка появляется, и проматываем немую заставку, пока идёт непропускаемый ролик.
    /// </summary>
    public const string YoutubeAdSkipScript = """
    (() => {
      if (location.hostname.indexOf('youtube.com') === -1) return;
      if (window.__strataYtAd) return;
      window.__strataYtAd = true;
      var speeding = false, savedMuted = false, savedRate = 1;
      setInterval(function () {
        var skips = document.querySelectorAll('.ytp-ad-skip-button, .ytp-skip-ad-button, .ytp-ad-skip-button-modern, .ytp-ad-overlay-close-button');
        for (var i = 0; i < skips.length; i++) { try { skips[i].click(); } catch (e) {} }

        var player = document.querySelector('.html5-video-player');
        var video = document.querySelector('video');
        var adShowing = !!(player && player.classList.contains('ad-showing'));
        if (!video) return;
        if (adShowing) {
          if (!speeding) { speeding = true; savedMuted = video.muted; savedRate = video.playbackRate || 1; video.muted = true; }
          if (video.playbackRate !== 16) { try { video.playbackRate = 16; } catch (e) {} }
        } else if (speeding) {
          speeding = false;
          try { video.playbackRate = savedRate; video.muted = savedMuted; } catch (e) {}
        }
      }, 250);
    })();
    """;
}
