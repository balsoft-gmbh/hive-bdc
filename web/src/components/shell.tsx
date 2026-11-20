"use client";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useState, type ComponentType } from "react";
import {
  Cable,
  ChevronRight,
  ChevronsUpDown,
  FileInput,
  FileOutput,
  History,
  LayoutDashboard,
  LoaderCircle,
  LogOut,
  Menu,
  Moon,
  ServerCog,
  Sun,
} from "lucide-react";
import { Menu as FluentMenu, MenuItemLink, MenuList, MenuPopover, MenuTrigger } from "@fluentui/react-components";
import { initials, licenceLine, topRole } from "@/lib/logic";
import { updateBadge } from "@/lib/updatelogic";
import { BrandLogo } from "./brand-logo";
import { isSetupPath, useConsole } from "./console-context";
import { useCrumbLevels } from "./crumbs";

// The CRM shell (balsoft-crm crm-workspace.tsx, as the Hive ERP copies it): sidebar with the brand, the instance card,
// the navigation and the account control; top bar with breadcrumb, the instance state and the theme switch. The
// logo and the name "Hive BDC" are one link to the Overview (Ertugrul 2026-11-19).

type Icon = ComponentType<{ size?: number; strokeWidth?: number; className?: string }>;
type Page = { href: string; title: string; sub?: string; icon: Icon };

export const PAGES: { label: string; pages: Page[] }[] = [
  {
    label: "WORK",
    pages: [
      { href: "/", title: "Overview", icon: LayoutDashboard },
      { href: "/history/", title: "Document history", icon: History },
    ],
  },
  {
    label: "SET UP",
    pages: [
      { href: "/outbound/", title: "Outbound", sub: "from your ERP", icon: FileOutput },
      { href: "/inbound-flows/", title: "Inbound", sub: "into your ERP", icon: FileInput },
      { href: "/connections/", title: "Connections", icon: Cable },
      { href: "/service/", title: "Service", icon: ServerCog },
    ],
  },
];

const ICON = { size: 18, strokeWidth: 1.75 };

/** The page a path belongs to: "/outbound/type/" belongs to Outbound. */
export function pageOf(pathname: string): Page | undefined {
  const path = pathname.endsWith("/") ? pathname : `${pathname}/`;
  const all = PAGES.flatMap((g) => g.pages);
  return all.find((p) => p.href !== "/" && path.startsWith(p.href)) ?? all.find((p) => p.href === "/" && path === "/");
}

export function instanceName() {
  return typeof window === "undefined" ? "" : window.location.hostname.split(".")[0];
}

function useTheme() {
  const [dark, setDark] = useState(false);
  useEffect(() => setDark(document.documentElement.dataset.theme === "dark"), []);
  const toggle = () => {
    const next = dark ? "light" : "dark";
    document.documentElement.dataset.theme = next;
    try {
      localStorage.setItem("hivebdc-theme", next);
    } catch {
      // Storage can be blocked; the theme then lasts for this page only.
    }
    setDark(!dark);
  };
  return { dark, toggle };
}

function Brand() {
  return (
    <Link href="/" className="brand" aria-label="Hive BDC, back to the overview" title="Back to the overview">
      <BrandLogo />
      <span>Hive BDC</span>
    </Link>
  );
}

function AccountMenu({ placement }: { placement: "sidebar" | "topbar" }) {
  const { me } = useConsole();
  const role = me ? topRole(me.roles) || "No role" : "Connecting";
  return (
    <FluentMenu positioning={placement === "sidebar" ? "above-start" : "below-end"}>
      <MenuTrigger disableButtonEnhancement>
        <button className={placement === "sidebar" ? "sidebar-account" : "avatar-button"} aria-label="Account menu" title={me?.name}>
          <span className={`avatar gold${placement === "topbar" ? " small" : ""}`}>{me ? initials(me.name) : "B"}</span>
          {placement === "sidebar" && (
            <>
              <span className="sidebar-account-text">
                <strong>{me?.name ?? "Hive BDC"}</strong>
                <small>{role}</small>
              </span>
              <ChevronsUpDown size={15} />
            </>
          )}
        </button>
      </MenuTrigger>
      <MenuPopover>
        <MenuList>
          <MenuItemLink href="/.auth/logout" icon={<LogOut size={16} />}>
            Sign out
          </MenuItemLink>
        </MenuList>
      </MenuPopover>
    </FluentMenu>
  );
}

function InstanceState() {
  const { status, config } = useConsole();
  if (!status) return null;
  const running = Boolean(status.running);
  const paused = running && Boolean(config?.paused);
  const licence = licenceLine(status.license);
  const text = paused ? (config?.setupOpen ? "Paused until the set-up is finished" : "Paused") : running ? "Running" : "Stopped";
  return (
    <span className={`instance-state${running && !paused ? "" : " off"}`} title={licence.text}>
      <span className="dot" aria-hidden="true" />
      {text}
    </span>
  );
}

/** Hive BDC > the page > the levels the page sets; every level but the last is a link (design version 2, V4). */
function Breadcrumb({ page }: { page: Page | undefined }) {
  const levels = useCrumbLevels();
  const title = page?.title ?? "Page not found";
  return (
    <nav className="breadcrumb" aria-label="Breadcrumb">
      <Link href="/">Hive BDC</Link> <ChevronRight size={14} />
      {levels.length === 0 || !page ? (
        <strong aria-current="page">{title}</strong>
      ) : (
        <>
          <Link href={page.href}>{title}</Link>
          {levels.map((level, i) => (
            <span key={`${level.label}-${i}`} className="crumb-level">
              <ChevronRight size={14} />
              {i < levels.length - 1 && level.href ? (
                <Link href={level.href}>{level.label}</Link>
              ) : (
                <strong aria-current="page">{level.label}</strong>
              )}
            </span>
          ))}
        </>
      )}
    </nav>
  );
}

function Loading() {
  return (
    <div className="loading-state">
      <LoaderCircle className="spin" size={26} />
      <h2>Opening Hive BDC</h2>
      <p>Reading the state of this instance.</p>
    </div>
  );
}

function ErrorBanner() {
  const { error, refresh } = useConsole();
  if (!error) return null;
  return (
    <div className="error-banner" role="alert">
      <span>{error}</span>
      <button className="button secondary" onClick={() => void refresh()}>
        Reload
      </button>
    </div>
  );
}

export function Shell({ children }: { children: React.ReactNode }) {
  const pathname = usePathname();
  const { me, status, updates, loaded } = useConsole();
  const { dark, toggle } = useTheme();
  const [mobileMenu, setMobileMenu] = useState(false);
  const [compact, setCompact] = useState(false);
  const [instance, setInstance] = useState("");
  useEffect(() => {
    setInstance(instanceName());
    const query = matchMedia("(max-width: 960px)");
    const sync = () => setCompact(query.matches);
    sync();
    query.addEventListener("change", sync);
    return () => query.removeEventListener("change", sync);
  }, []);
  useEffect(() => setMobileMenu(false), [pathname]);

  const themeButton = (
    <button className="icon-button" aria-label={dark ? "Switch to light theme" : "Switch to dark theme"} onClick={toggle}>
      {dark ? <Sun size={19} /> : <Moon size={19} />}
    </button>
  );

  // The set-up assistant takes the whole screen: brand, account and the assistant, no navigation (failure case 9).
  if (isSetupPath(pathname)) {
    return (
      <div className="setup-screen">
        <header className="setup-topbar">
          <Brand />
          <div className="topbar-right">
            <InstanceState />
            {themeButton}
            <AccountMenu placement="topbar" />
          </div>
        </header>
        <main id="main-content" className="setup-main">
          <ErrorBanner />
          {loaded && me ? children : <Loading />}
        </main>
      </div>
    );
  }

  const page = pageOf(pathname);
  const badge = updateBadge(updates);
  const holder = status?.license?.customer as string | undefined;
  return (
    <div className="workspace">
      <a className="skip-link" href="#main-content">
        Skip to content
      </a>
      {mobileMenu && <button className="sidebar-backdrop" aria-label="Close navigation" onClick={() => setMobileMenu(false)} />}
      <aside className={`sidebar ${mobileMenu ? "mobile-open" : ""}`} inert={compact && !mobileMenu} aria-hidden={compact && !mobileMenu}>
        <Brand />
        <div className="instance-card" title={holder ? `${holder} · ${instance}` : instance}>
          <span className="instance-mark">
            <ServerCog {...ICON} />
          </span>
          <span className="instance-text">
            <strong>{holder || "Hive BDC"}</strong>
            <span>{instance}</span>
          </span>
        </div>
        <nav aria-label="Main navigation">
          {PAGES.map((group) => (
            <div className="nav-group" key={group.label}>
              <div className="nav-label">{group.label}</div>
              {group.pages.map((p) => {
                const current = page?.href === p.href;
                return (
                  <Link key={p.href} href={p.href} className={`nav-link${current ? " active" : ""}`} aria-current={current ? "page" : undefined}>
                    <span className="nav-icon">
                      <p.icon {...ICON} />
                    </span>
                    <span className="nav-text">
                      <span>{p.title}</span>
                      {p.sub && <small>{p.sub}</small>}
                    </span>
                    {p.href === "/service/" && badge ? (
                      <span className="nav-count update-count" title="Update available">
                        {badge}
                        <span className="sr-only"> update available</span>
                      </span>
                    ) : null}
                  </Link>
                );
              })}
            </div>
          ))}
        </nav>
        <div className="sidebar-bottom">
          <AccountMenu placement="sidebar" />
        </div>
      </aside>
      <div className="main-shell" inert={compact && mobileMenu}>
        <header className="topbar">
          <button className="icon-button mobile-toggle" aria-label="Open navigation" onClick={() => setMobileMenu(true)}>
            <Menu size={20} />
          </button>
          <Breadcrumb page={page} />
          <div className="topbar-right">
            <InstanceState />
            {themeButton}
            <AccountMenu placement="topbar" />
          </div>
        </header>
        <main id="main-content" tabIndex={-1}>
          <ErrorBanner />
          {loaded && me ? children : <Loading />}
        </main>
      </div>
    </div>
  );
}
