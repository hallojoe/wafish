import { lazy, Suspense, useEffect, useRef, useState } from "react";
import {
  BrowserRouter,
  Link,
  NavLink,
  Route,
  Routes,
  useLocation,
} from "react-router";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { ThemeProvider, useTheme } from "next-themes";
import { Network, Menu, Moon, Sun, LogOut, ChevronRight } from "lucide-react";
import { config, ApiError } from "@/api/client";
import { LocaleProvider, useLocale } from "@/locales/i18n";
import { AuthProvider, useAuth } from "@/components/auth";
import { Button } from "@/components/ui/button";
import {
  Sheet,
  SheetContent,
  SheetHeader,
  SheetTitle,
  SheetDescription,
} from "@/components/ui/sheet";
import { Toaster } from "@/components/ui/sonner";
import { TooltipProvider } from "@/components/ui/tooltip";
const Overview = lazy(() =>
  import("@/pages/observe").then((m) => ({ default: m.OverviewPage })),
);
const Traffic = lazy(() =>
  import("@/pages/observe").then((m) => ({ default: m.TrafficPage })),
);
const Logs = lazy(() =>
  import("@/pages/observe").then((m) => ({ default: m.LogsPage })),
);
const Destinations = lazy(() =>
  import("@/pages/observe").then((m) => ({ default: m.DestinationsPage })),
);
const Resources = lazy(() =>
  import("@/pages/observe").then((m) => ({ default: m.ResourcesPage })),
);
const Configuration = lazy(() =>
  import("@/pages/configuration").then((m) => ({
    default: m.ConfigurationPage,
  })),
);
const Import = lazy(() =>
  import("@/pages/configuration").then((m) => ({ default: m.ImportPage })),
);
const History = lazy(() =>
  import("@/pages/history").then((m) => ({ default: m.HistoryPage })),
);
const Plugins = lazy(() =>
  import("@/pages/plugins").then((m) => ({ default: m.PluginsPage })),
);
const Bindings = lazy(() =>
  import("@/pages/plugins").then((m) => ({ default: m.BindingsPage })),
);
const Presets = lazy(() =>
  import("@/pages/plugins").then((m) => ({ default: m.PresetsPage })),
);
const System = lazy(() =>
  import("@/pages/settings").then((m) => ({ default: m.SystemPage })),
);
const Notifications = lazy(() =>
  import("@/pages/settings").then((m) => ({ default: m.NotificationsPage })),
);
const Account = lazy(() =>
  import("@/pages/settings").then((m) => ({ default: m.AccountPage })),
);
const Integrations = lazy(() =>
  import("@/pages/settings").then((m) => ({ default: m.IntegrationPage })),
);
const Assistant = lazy(() =>
  import("@/pages/assistant").then((m) => ({ default: m.AssistantPage })),
);
const navigation = [
  { label: "Overview", path: "/", items: [] },
  {
    label: "Configuration",
    path: "/configuration",
    items: [
      ["Routes", "routes"],
      ["Clusters", "clusters"],
      ["History", "history"],
      ["Import & export", "import"],
    ],
  },
  {
    label: "Observe",
    path: "/observe",
    items: [
      ["Logs", "logs"],
      ["Traffic", "traffic"],
      ["Destinations", "destinations"],
      ["Audit", "audit"],
    ],
  },
  {
    label: "Extensions",
    path: "/extensions",
    items: [
      ["Plugins", "plugins"],
      ["Bindings", "bindings"],
      ["Presets", "presets"],
      ["Resources", "resources"],
    ],
  },
  {
    label: "Settings",
    path: "/settings",
    items: [
      ["System", "system"],
      ["Notifications", "notifications"],
      ["Account", "account"],
      ...(config.ai ? [["AI configuration", "integrations"]] : []),
    ],
  },
];
function Shell() {
  const { t } = useLocale();
  const { resolvedTheme, setTheme } = useTheme();
  const { logout } = useAuth();
  const location = useLocation();
  const [mobile, setMobile] = useState(false);
  const main = useRef<HTMLElement>(null);
  const group =
    navigation.find(
      (item) => item.path !== "/" && location.pathname.startsWith(item.path),
    ) ?? navigation[0];
  useEffect(() => {
    document.title = `${t(group.label)} · WafIsh`;
    main.current?.focus();
  }, [location.pathname, t, group.label]);
  function nav() {
    return navigation.map((item) => (
      <NavLink
        key={item.path}
        to={item.items.length ? `${item.path}/${item.items[0][1]}` : "/"}
        className={group.path === item.path ? "nav-link active" : "nav-link"}
        onClick={() => setMobile(false)}
      >
        {t(item.label)}
      </NavLink>
    ));
  }
  return (
    <>
      <a className="skip-link" href="#main">
        {t("Skip to content")}
      </a>
      <header className="app-header">
        <div className="header-inner">
          <Link className="brand" to="/" aria-label={t("Gateway")}>
            <span className="brand-mark">
              <Network className="size-5" />
            </span>
            <span>WafIsh</span>
          </Link>
          <span className="environment-label">{t("Administration")}</span>
          <div className="header-actions">
            <Button
              variant="ghost"
              size="icon"
              aria-label={t("Switch theme")}
              onClick={() =>
                setTheme(resolvedTheme === "dark" ? "light" : "dark")
              }
            >
              {resolvedTheme === "dark" ? <Sun /> : <Moon />}
            </Button>
            <Button
              variant="ghost"
              size="icon"
              aria-label={t("Sign out")}
              onClick={() => void logout()}
            >
              <LogOut />
            </Button>
            <Button
              variant="ghost"
              size="icon"
              className="mobile-menu"
              aria-label={t("Open navigation")}
              onClick={() => setMobile(true)}
            >
              <Menu />
            </Button>
          </div>
        </div>
        <nav aria-label={t("Administration")} className="primary-nav">
          {nav()}
          {config.ai && (
            <NavLink to="/assistant" className="nav-link assistant-link">
              {t("Assistant")}
            </NavLink>
          )}
        </nav>
      </header>
      <div className="workspace">
        <aside
          className={
            group.items.length
              ? "workspace-sidebar"
              : "workspace-sidebar overview-sidebar"
          }
        >
          <p className="eyebrow mb-4">{t(group.label)}</p>
          <nav aria-label={t(group.label)}>
            {group.items.length ? (
              group.items.map(([label, path]) => (
                <NavLink
                  key={path}
                  to={`${group.path}/${path}`}
                  className={({ isActive }) =>
                    `subnav-link ${isActive ? "active" : ""}`
                  }
                >
                  {t(label)}
                  <ChevronRight className="size-3" />
                </NavLink>
              ))
            ) : (
              <>
                <Link className="subnav-link" to="/configuration/routes">
                  {t("View routes")}
                  <ChevronRight className="size-3" />
                </Link>
                <Link className="subnav-link" to="/observe/logs">
                  {t("View logs")}
                  <ChevronRight className="size-3" />
                </Link>
              </>
            )}
          </nav>
        </aside>
        <main id="main" tabIndex={-1} ref={main} className="main-content">
          <Suspense fallback={<div role="status">{t("Loading")}</div>}>
            <Routes>
              <Route path="/" element={<Overview />} />
              <Route
                path="/configuration/routes"
                element={<Configuration key="routes" kind="routes" />}
              />
              <Route
                path="/configuration/clusters"
                element={<Configuration key="clusters" kind="clusters" />}
              />
              <Route path="/configuration/history" element={<History />} />
              <Route path="/configuration/import" element={<Import />} />
              <Route path="/observe/logs" element={<Logs />} />
              <Route
                path="/observe/audit"
                element={<Logs key="audit" audit />}
              />
              <Route path="/observe/traffic" element={<Traffic />} />
              <Route path="/observe/destinations" element={<Destinations />} />
              <Route path="/extensions/plugins" element={<Plugins />} />
              <Route path="/extensions/bindings" element={<Bindings />} />
              <Route path="/extensions/presets" element={<Presets />} />
              <Route path="/extensions/resources" element={<Resources />} />
              <Route path="/settings/system" element={<System />} />
              <Route
                path="/settings/notifications"
                element={<Notifications />}
              />
              <Route path="/settings/account" element={<Account />} />
              <Route path="/settings/integrations" element={<Integrations />} />
              <Route path="/assistant" element={<Assistant />} />
              <Route
                path="*"
                element={
                  <div>
                    <h1>{t("No results")}</h1>
                    <Link to="/">{t("Overview")}</Link>
                  </div>
                }
              />
            </Routes>
          </Suspense>
          <footer className="workspace-footer">
            <span>WafIsh</span>
            <span>{t("Manage traffic. Understand every change.")}</span>
          </footer>
        </main>
      </div>
      <Sheet open={mobile} onOpenChange={setMobile}>
        <SheetContent side="left">
          <SheetHeader>
            <SheetTitle>{t("Gateway workspace")}</SheetTitle>
            <SheetDescription>{t("Administration")}</SheetDescription>
          </SheetHeader>
          <nav className="grid gap-2 p-4" aria-label={t("Open navigation")}>
            {nav()}
            {config.ai && (
              <Link
                onClick={() => setMobile(false)}
                className="nav-link"
                to="/assistant"
              >
                {t("Assistant")}
              </Link>
            )}
          </nav>
        </SheetContent>
      </Sheet>
    </>
  );
}
const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 5000,
      refetchOnWindowFocus: false,
      retry: (count, error) =>
        !(error instanceof ApiError && error.status < 500) && count < 1,
    },
    mutations: { retry: false },
  },
});
export default function App() {
  return (
    <ThemeProvider
      disableTransitionOnChange
      attribute="class"
      defaultTheme="system"
      storageKey="gateway-theme"
    >
      <LocaleProvider>
        <QueryClientProvider client={queryClient}>
          <TooltipProvider>
            <AuthProvider>
              <BrowserRouter basename={config.uiBase || "/"}>
                <Shell />
              </BrowserRouter>
            </AuthProvider>
            <Toaster richColors />
          </TooltipProvider>
        </QueryClientProvider>
      </LocaleProvider>
    </ThemeProvider>
  );
}
