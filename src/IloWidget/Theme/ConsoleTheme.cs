using System.Text.Json;

namespace IloWidget.Theme;

/// <summary>
/// Halo UNSC restyle of HPE's HTML5 console page (/irc.html). It is injected into the page inside the
/// widget's console window only; nothing on the iLO is modified. Selectors come from the iLO 4 (2.8x) page.
/// The page sets many colours with inline styles and late-loaded stylesheets, hence the !important rules.
/// </summary>
public static class ConsoleTheme
{
    public const string Css = """
        :root {
          --h-accent: #4FD8FF; --h-bright: #8CECFF; --h-dim: #6FA8C7; --h-text: #D6F3FF;
          --h-panel: #071320; --h-bar: #0A1C2B; --h-field: #0A2233; --h-hover: rgba(79,216,255,.2);
          --h-font: Bahnschrift, 'Segoe UI', sans-serif;
        }

        /* the window has its own maximize and close buttons */
        .btnVideoFullScreen, .btnVideoContract, .btnVideoClose { display: none !important; }
        .control .rightControl { width: 108px; }
        /* the iLO address is already in the window's title bar */
        .control .middleControl { visibility: hidden !important; }

        /* top toolbar */
        .control {
          background: var(--h-bar) !important; opacity: 1 !important; border-radius: 0 !important;
          border-bottom: 1px solid rgba(79,216,255,.6); color: var(--h-dim) !important; font-family: var(--h-font) !important;
        }
        .control .middleControl, .control .middleControl * {
          color: var(--h-dim) !important; font-family: var(--h-font) !important; letter-spacing: .06em; text-transform: uppercase;
        }
        /* icons are drawn with mixed fills and strokes; tint the rendered result instead of guessing which */
        .control div.btn svg { filter: sepia(1) saturate(6) hue-rotate(155deg) brightness(.95); }
        .control div.btn:hover svg { filter: sepia(1) saturate(6) hue-rotate(155deg) brightness(1.25) drop-shadow(0 0 4px var(--h-accent)); }

        /* drop-down menus (jQuery UI) */
        ul.hasmenu, ul.hasmenu ul, .ui-menu {
          background: var(--h-panel) !important; border: 1px solid var(--h-accent) !important; border-radius: 0 !important;
          box-shadow: 0 0 14px rgba(79,216,255,.35) !important; color: var(--h-text) !important; font-family: var(--h-font) !important;
        }
        /* HPE's own sheet paints menu rows light grey with !important at (0,3,0) specificity
           (.hasmenu.ui-menu .ui-menu-item), so these selectors are deliberately heavier. */
        html body .hasmenu.ui-menu, html body .hasmenu .ui-menu, html body ul.hasmenu.ui-widget-content, html body .hasmenu ul {
          background: var(--h-panel) !important; background-color: var(--h-panel) !important;
          border: 1px solid var(--h-accent) !important; color: var(--h-text) !important;
        }
        html body .hasmenu.ui-menu .ui-menu-item, html body .hasmenu .ui-menu .ui-menu-item, html body .hasmenu li,
        .ui-menu .ui-menu-item, .ui-menu li {
          background: transparent !important; background-color: transparent !important;
          border-color: transparent !important; color: var(--h-text) !important;
        }
        html body .hasmenu li.ui-menu-divider { background-color: rgba(111,168,199,.4) !important; }
        html body .hasmenu .ui-menu-item-wrapper, html body .hasmenu li > div, html body .hasmenu li label, html body .hasmenu li label div,
        html body .hasmenu li span, html body .hasmenu li a {
          color: var(--h-text) !important; background: transparent !important; background-color: transparent !important;
          border-color: transparent !important; font-family: var(--h-font) !important;
        }
        html body .hasmenu .ui-state-active, html body .hasmenu .ui-widget-content .ui-state-active,
        html body .hasmenu li > div.ui-state-active, html body .hasmenu .ui-state-focus, html body .hasmenu .ui-state-hover {
          background: var(--h-hover) !important; background-color: var(--h-hover) !important;
          color: #fff !important; border-color: transparent !important; margin: 0 !important;
        }
        html body .hasmenu .ui-state-active span, html body .hasmenu .ui-state-active div { color: #fff !important; }
        /* compact rows */
        html body .hasmenu.ui-menu, html body ul.hasmenu, html body .hasmenu ul { padding: 2px 0 !important; }
        html body .hasmenu.ui-menu .ui-menu-item, html body .hasmenu .ui-menu .ui-menu-item, html body .hasmenu li.ui-menu-item {
          margin: 0 !important; padding: 0 !important; min-height: 0 !important; height: auto !important; line-height: 1.25 !important;
        }
        html body .hasmenu .ui-menu-item-wrapper, html body .hasmenu li > div, html body .hasmenu li label {
          margin: 0 !important; padding: 3px 12px !important; min-height: 0 !important; height: auto !important;
          line-height: 1.25 !important; font-size: 13px !important;
        }
        html body .hasmenu li.ui-menu-divider, html body .hasmenu hr.ui-menu-item-wrapper {
          margin: 3px 0 !important; padding: 0 !important; height: 1px !important; min-height: 0 !important;
        }
        .ui-menu .ui-state-disabled, .ui-menu .ui-state-disabled > div { color: var(--h-dim) !important; opacity: .55 !important; }
        .ui-menu .ui-widget-header, .hasmenu.irc .ui-widget-header {
          background: var(--h-bar) !important; border: 0 !important; border-bottom: 1px solid rgba(79,216,255,.35) !important;
        }
        .ui-menu .ui-widget-header > div { color: var(--h-accent) !important; opacity: 1 !important; text-transform: uppercase; letter-spacing: .08em; }
        .ui-menu hr.ui-menu-item-wrapper { background: rgba(111,168,199,.4) !important; }
        .ui-menu .ui-icon { filter: sepia(1) saturate(6) hue-rotate(155deg) brightness(1.4); }
        .hasmenu svg.control-icon-checkmark, .hasmenu svg.control-icon-checkmark polyline { stroke: var(--h-accent) !important; }
        .hasmenu svg.control-icon-checkmark { fill: none !important; }
        .hasmenu svg.control-icon-eject { fill: var(--h-accent) !important; stroke: var(--h-accent) !important; }

        /* virtual keyboard keys */
        .ui-menu button.menu-vkey-button {
          border: 1px solid var(--h-accent) !important; border-radius: 0 6px 0 6px !important;
          background: var(--h-field) !important; color: var(--h-accent) !important; font-family: var(--h-font) !important;
        }
        .ui-menu button.menu-vkey-button:hover { background: var(--h-hover) !important; color: #fff !important; }
        .ui-menu button.menu-vkey-button:active { background: var(--h-accent) !important; color: #061018 !important; }

        /* dialogs (virtual media URL and others) */
        .ui-dialog {
          background: var(--h-panel) !important; border: 1px solid var(--h-accent) !important; border-radius: 0 !important;
          box-shadow: 0 0 18px rgba(79,216,255,.4) !important; color: var(--h-text) !important; font-family: var(--h-font) !important; padding: 0 !important;
        }
        .ui-dialog .ui-dialog-titlebar {
          background: var(--h-bar) !important; border: 0 !important; border-bottom: 1px solid rgba(79,216,255,.5) !important;
          border-radius: 0 !important; color: var(--h-accent) !important;
        }
        .ui-dialog .ui-dialog-title { color: var(--h-accent) !important; text-transform: uppercase; letter-spacing: .08em; font-family: var(--h-font) !important; }
        .ui-dialog .ui-dialog-content, .ui-dialog .ui-widget-content, .ui-dialog .form, .ui-dialog .form-fields,
        .ui-dialog .form-field, .ui-dialog [class*=background-color-index-] {
          background: transparent !important; color: var(--h-text) !important;
        }
        .ui-dialog .ui-dialog-buttonpane { background: transparent !important; border-top: 1px solid rgba(111,168,199,.35) !important; }
        .ui-dialog-titlebar-close { background: transparent !important; border: 0 !important; }
        .ui-dialog-titlebar-close svg { stroke: var(--h-accent) !important; }
        .videoContainer #vm-url-form .form-field { border: 0 !important; }
        .videoContainer #vm-url-form .form-field__label, .form-field__label { color: var(--h-dim) !important; text-transform: uppercase; font-family: var(--h-font) !important; }
        .videoContainer #vm-url-form input, .ui-dialog input[type=text], .ui-dialog input[type=password], .ui-dialog select, .ui-dialog textarea {
          background: var(--h-field) !important; color: var(--h-text) !important; border: 1px solid var(--h-dim) !important;
          border-bottom-width: 2px !important; border-radius: 0 !important; font-family: var(--h-font) !important;
        }
        .ui-dialog input:focus, .ui-dialog select:focus { border-color: var(--h-accent) !important; outline: none !important; box-shadow: none !important; }

        /* buttons in dialogs and message boxes */
        .ui-dialog .ui-button, .ui-dialog button.button, .msgbox-wrapper .button, .msgbox-wrapper button {
          background: var(--h-field) !important; color: var(--h-accent) !important; border: 1px solid var(--h-accent) !important;
          border-radius: 0 8px 0 8px !important; box-shadow: none !important; font-family: var(--h-font) !important; text-transform: uppercase;
        }
        .ui-dialog .ui-button:hover, .ui-dialog button.button:hover, .msgbox-wrapper .button:hover, .msgbox-wrapper button:hover {
          background: var(--h-hover) !important; color: #fff !important; box-shadow: none !important;
        }
        .ui-dialog .ui-dialog-titlebar-close.ui-button { border: 0 !important; background: transparent !important; }

        /* message boxes */
        .msgbox-wrapper {
          background: var(--h-panel) !important; border: 1px solid var(--h-accent) !important; border-radius: 0 !important;
          box-shadow: 0 0 18px rgba(79,216,255,.4) !important; font-family: var(--h-font) !important;
        }
        .msgbox-title { color: var(--h-accent) !important; text-transform: uppercase; letter-spacing: .08em; font-family: var(--h-font) !important; }
        .msgbox-text, .msgbox-text p, .msgbox-timer-msg { color: var(--h-text) !important; font-family: var(--h-font) !important; }
        .msgbox-close:hover { outline: 1px solid var(--h-accent) !important; }
        .msgbox-close svg { fill: var(--h-accent) !important; stroke: var(--h-accent) !important; }

        /* notifications */
        .toast-item { background: var(--h-panel) !important; border: 1px solid var(--h-accent) !important; opacity: 1 !important; }
        .toast-item-msg { color: var(--h-text) !important; font-family: var(--h-font) !important; }
        .toast-item-close svg { fill: var(--h-accent) !important; }

        /* status bar; health, activity and power lamps keep their own colours because the colour is the information */
        .irc-statusbar {
          background: var(--h-bar) !important; border-top: 1px solid rgba(79,216,255,.6); color: var(--h-dim) !important;
          font-family: var(--h-font) !important; font-weight: 400 !important; letter-spacing: .05em;
        }
        .irc-statusbar .status-panel, .irc-statusbar #encryption-type { color: var(--h-dim) !important; }
        .irc-statusbar .screen-capture svg, .irc-statusbar .encryption svg { filter: sepia(1) saturate(6) hue-rotate(155deg) brightness(1.1); }
        .irc-statusbar .screen-capture:hover svg { filter: sepia(1) saturate(6) hue-rotate(155deg) brightness(1.5) drop-shadow(0 0 3px var(--h-accent)); }

        /* scrollbars inside menus and dialogs */
        ::-webkit-scrollbar { width: 10px; height: 10px; }
        ::-webkit-scrollbar-track { background: var(--h-panel); }
        ::-webkit-scrollbar-thumb { background: var(--h-dim); }
        ::-webkit-scrollbar-thumb:hover { background: var(--h-accent); }
        """;

    /// <summary>
    /// Script for AddScriptToExecuteOnDocumentCreatedAsync: adds the style sheet as early as possible and
    /// keeps it last in the head, so page stylesheets that load later do not win on source order.
    /// </summary>
    public static string InjectionScript => $$"""
        (function () {
          var css = {{JsonSerializer.Serialize(Css)}};
          function add() {
            var s = document.getElementById('ilowidget-style');
            if (!s) {
              s = document.createElement('style');
              s.id = 'ilowidget-style';
              s.textContent = css;
            }
            var host = document.head || document.documentElement;
            if (host && host.lastElementChild !== s) host.appendChild(s);
          }
          // at document creation there may be no root element yet
          add();
          // wait for <head>, then re-append the style whenever a later node lands after it
          var watching = false;
          function watch() {
            if (watching || !document.head) return;
            watching = true;
            add();
            new MutationObserver(add).observe(document.head, { childList: true });
          }
          watch();
          if (!watching) new MutationObserver(function (_, o) { watch(); if (watching) o.disconnect(); })
            .observe(document, { childList: true, subtree: true });
          document.addEventListener('DOMContentLoaded', add);
          window.addEventListener('load', add);
        })();
        """;
}
