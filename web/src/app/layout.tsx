import type { Metadata } from "next";
import "./globals.css";
import "./bdc.css";
import "./bdc-outbound.css";
import "./bdc-service.css";
import "./bdc-setup.css";
import { UiProvider } from "@/components/ui/provider";
import { ConsoleProvider } from "@/components/console-context";
import { CrumbProvider } from "@/components/crumbs";
import { Shell } from "@/components/shell";

export const metadata: Metadata = {
  title: "Hive BDC",
  description:
    "Hive BDC web console: e-invoices and documents from Dynamics 365, delivered from your own Azure subscription.",
  icons: {
    icon: [
      { url: "/icon-32.png", sizes: "32x32", type: "image/png" },
      { url: "/hive-bdc-icon.svg", type: "image/svg+xml" },
    ],
    apple: { url: "/apple-touch-icon.png", sizes: "180x180" },
  },
  robots: { index: false, follow: false },
};

// Applies the saved or system theme before the first paint. Its hash is in the content security policy
// (scripts/csp-hashes.ts), as every inline script of the export is.
const themeScript = `try{var t=localStorage.getItem("hivebdc-theme");if(!t){t=matchMedia("(prefers-color-scheme: dark)").matches?"dark":"light"}document.documentElement.dataset.theme=t}catch(e){}`;

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en" suppressHydrationWarning>
      <head>
        <script dangerouslySetInnerHTML={{ __html: themeScript }} />
        <link rel="preload" href="/fonts/dm-sans.ttf" as="font" type="font/ttf" crossOrigin="anonymous" />
        <link rel="preload" href="/fonts/manrope.ttf" as="font" type="font/ttf" crossOrigin="anonymous" />
      </head>
      <body>
        <UiProvider>
          <ConsoleProvider>
            <CrumbProvider>
              <Shell>{children}</Shell>
            </CrumbProvider>
          </ConsoleProvider>
        </UiProvider>
      </body>
    </html>
  );
}
