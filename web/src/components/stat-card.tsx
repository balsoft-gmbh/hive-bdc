import Link from "next/link";
import type { ComponentType, ReactNode } from "react";

// The CRM's stat card (balsoft-crm overview-dashboard.tsx): label top left, a small icon top right, the value and
// one note. A card with href is a link.

type Icon = ComponentType<{ size?: number; strokeWidth?: number }>;

export function StatCard({
  title,
  value,
  note,
  icon: Icon,
  accent,
  href,
}: {
  title: string;
  value: ReactNode;
  note?: ReactNode;
  icon: Icon;
  accent: "gold" | "blue" | "purple" | "green";
  href?: string;
}) {
  const body = (
    <>
      <div className="stat-top">
        <span>{title}</span>
        <span className={`stat-icon ${accent}`}>
          <Icon size={19} strokeWidth={1.75} />
        </span>
      </div>
      <strong>{value}</strong>
      {note && <small>{note}</small>}
    </>
  );
  return href ? (
    <Link className="stat-card stat-link" href={href}>
      {body}
    </Link>
  ) : (
    <section className="stat-card">{body}</section>
  );
}
