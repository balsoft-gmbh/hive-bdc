import type { NextConfig } from "next";

// Production builds are a static export (out/) that the Hive BDC web host serves from wwwroot/console
// (Hosting/ConsoleFiles.cs), so console and API share one origin and one App Service sign-in. In development the
// Next server forwards /api to a running Hive BDC web host.
const production = process.env.NODE_ENV === "production";
const api = process.env.HIVEBDC_API_URL ?? "http://127.0.0.1:5080";

const config: NextConfig = production
  ? {
      output: "export",
      trailingSlash: true,
      reactStrictMode: true,
      poweredByHeader: false,
      images: { unoptimized: true },
    }
  : {
      reactStrictMode: true,
      async rewrites() {
        return [{ source: "/api/:path*", destination: `${api}/api/:path*` }];
      },
    };

export default config;
