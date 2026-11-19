import type { ReactNode } from "react";

/** The CRM page heading: eyebrow, title, one line of context and the page actions. */
export function PageHeading({
  title,
  description,
  eyebrow = "HIVE BDC",
  actions,
}: {
  title: ReactNode;
  description?: ReactNode;
  eyebrow?: string;
  actions?: ReactNode;
}) {
  return (
    <div className="page-heading">
      <div>
        <div className="eyebrow">{eyebrow}</div>
        <h1>{title}</h1>
        {description && <p>{description}</p>}
      </div>
      {actions && <div className="heading-actions">{actions}</div>}
    </div>
  );
}
