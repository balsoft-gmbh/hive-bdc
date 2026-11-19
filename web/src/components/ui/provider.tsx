"use client";
import { useEffect, useState } from "react";
import { useServerInsertedHTML } from "next/navigation";
import {
  FluentProvider,
  RendererProvider,
  SSRProvider,
  createDOMRenderer,
  renderToStyleElements,
  webDarkTheme,
  webLightTheme,
} from "@fluentui/react-components";

// The CRM provider (balsoft-crm src/components/ui/provider.tsx) with its theme
// values. The ERP is a static export, so Fluent styles are also collected at
// build time and written into the HTML; the first paint already has them.
export function UiProvider({ children }: { children: React.ReactNode }) {
  const [renderer] = useState(() => createDOMRenderer());
  const [dark, setDark] = useState(false);
  useServerInsertedHTML(() => <>{renderToStyleElements(renderer)}</>);
  useEffect(() => {
    const sync = () =>
      setDark(document.documentElement.dataset.theme === "dark");
    sync();
    const observer = new MutationObserver(sync);
    observer.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ["data-theme"],
    });
    return () => observer.disconnect();
  }, []);
  const base = dark ? webDarkTheme : webLightTheme;
  return (
    <RendererProvider renderer={renderer}>
      <SSRProvider>
        <FluentProvider
          theme={{
            ...base,
            fontFamilyBase: '"DM Sans", sans-serif',
            colorBrandBackground: "#805b0b",
            colorBrandBackgroundHover: "#654807",
            colorBrandForeground1: dark ? "#f2c566" : "#805b0b",
            colorCompoundBrandStroke: "#805b0b",
          }}
          style={{ background: "transparent", color: "inherit" }}
        >
          {children}
        </FluentProvider>
      </SSRProvider>
    </RendererProvider>
  );
}
