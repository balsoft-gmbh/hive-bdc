"use client";
import { useEffect, useState, type ReactNode } from "react";
import {
  Popover,
  PopoverSurface,
  PopoverTrigger,
} from "@fluentui/react-components";
import {
  ArrowDown,
  ArrowUp,
  ArrowUpDown,
  Check,
  ListFilter,
  X,
} from "lucide-react";
// Copied from balsoft-crm src/components/ui/data-grid.tsx (commit 6ee0acd). The
// filter types come from the CRM query module there; they are declared here.
export type ColumnFilter =
  string | string[] | { from?: string; to?: string } | { empty: true };
export type ColumnFilters = Record<string, ColumnFilter>;
// Shared enterprise list grid: dense rows, sticky header and first column,
// every column sortable and filterable from its header menu, one empty state
// and an optional right-aligned action cell. Lists backed by the API pass the
// sort/filter state through to the server; small in-memory lists use
// `localQuery` so both behave the same.
export type FilterKind = "text" | "select" | "date" | "number";
export type GridColumn<T> = {
  key: string;
  label: string;
  /** Server column (or accessor key for local grids). Absent = not sortable/filterable. */
  sortKey?: string;
  filter?: FilterKind;
  options?: { value: string; label: string }[];
  align?: "left" | "right";
  /** Pixels, or a CSS width; pixel widths add up to the grid's minimum width, so it scrolls rather than squeezing. */
  width?: number | string;
  /** Drop the column below this viewport width instead of squeezing the grid. */
  hideBelow?: 1300 | 1400 | 1600 | 1800;
  render: (row: T) => ReactNode;
  /** Value used by localQuery for sorting and filtering. */
  value?: (row: T) => string | number | null | undefined;
};
export function describeFilter(
  column: { label: string; options?: { value: string; label: string }[] },
  filter: ColumnFilter,
) {
  if (typeof filter === "string") return `${column.label} contains "${filter}"`;
  if (Array.isArray(filter))
    return `${column.label}: ${filter
      .map((v) => column.options?.find((o) => o.value === v)?.label || v)
      .join(", ")}`;
  if ("empty" in filter) return `${column.label} is empty`;
  if (filter.from && filter.to)
    return `${column.label} ${filter.from} to ${filter.to}`;
  if (filter.from) return `${column.label} from ${filter.from}`;
  return `${column.label} up to ${filter.to}`;
}
function ColumnMenu<T>({
  column,
  sort,
  direction,
  filter,
  onSort,
  onFilter,
}: {
  column: GridColumn<T>;
  sort?: string;
  direction?: string;
  filter?: ColumnFilter;
  onSort?: (key: string, direction: "asc" | "desc") => void;
  onFilter?: (key: string, value: ColumnFilter | null) => void;
}) {
  const key = column.sortKey as string;
  const [open, setOpen] = useState(false);
  const [text, setText] = useState(typeof filter === "string" ? filter : "");
  const [from, setFrom] = useState(
    filter && typeof filter === "object" && "from" in filter
      ? filter.from || ""
      : "",
  );
  const [to, setTo] = useState(
    filter && typeof filter === "object" && "to" in filter
      ? filter.to || ""
      : "",
  );
  const [chosen, setChosen] = useState<string[]>(
    Array.isArray(filter) ? filter : [],
  );
  useEffect(() => {
    setText(typeof filter === "string" ? filter : "");
    setChosen(Array.isArray(filter) ? filter : []);
    if (filter && typeof filter === "object" && !Array.isArray(filter)) {
      setFrom("from" in filter ? filter.from || "" : "");
      setTo("to" in filter ? filter.to || "" : "");
    } else {
      setFrom("");
      setTo("");
    }
  }, [filter]);
  const active = sort === key;
  const apply = (value: ColumnFilter | null) => {
    onFilter?.(key, value);
    setOpen(false);
  };
  const filterable = Boolean(onFilter && column.filter);
  return (
    <Popover
      open={open}
      onOpenChange={(_, data) => setOpen(data.open)}
      positioning="below-start"
      withArrow={false}
    >
      <PopoverTrigger disableButtonEnhancement>
        <button
          type="button"
          className={`column-menu-button${filter ? " filtered" : ""}`}
          aria-label={`${column.label} column options`}
        >
          {filter ? <ListFilter size={13} /> : <ArrowUpDown size={13} />}
        </button>
      </PopoverTrigger>
      <PopoverSurface className="column-menu">
        {onSort && (
          <div className="column-menu-section">
            <button
              type="button"
              className="column-menu-item"
              aria-pressed={active && direction === "asc"}
              onClick={() => {
                onSort(key, "asc");
                setOpen(false);
              }}
            >
              <ArrowUp size={14} />
              Sort ascending
              {active && direction === "asc" && <Check size={14} />}
            </button>
            <button
              type="button"
              className="column-menu-item"
              aria-pressed={active && direction === "desc"}
              onClick={() => {
                onSort(key, "desc");
                setOpen(false);
              }}
            >
              <ArrowDown size={14} />
              Sort descending
              {active && direction === "desc" && <Check size={14} />}
            </button>
          </div>
        )}
        {filterable && (
          <form
            autoComplete="off"
            className="column-menu-section"
            onSubmit={(event) => {
              event.preventDefault();
              if (column.filter === "select")
                apply(chosen.length ? chosen : null);
              else if (column.filter === "text") apply(text.trim() || null);
              else
                apply(
                  from || to
                    ? { from: from || undefined, to: to || undefined }
                    : null,
                );
            }}
          >
            <span className="column-menu-label">Filter</span>
            {column.filter === "text" && (
              <input
                autoComplete="off"
                aria-label={`Filter ${column.label}`}
                placeholder="Contains"
                value={text}
                onChange={(e) => setText(e.target.value)}
                autoFocus
              />
            )}
            {column.filter === "select" && (
              <div className="column-menu-options">
                {(column.options || []).map((option) => (
                  <label key={option.value} className="check-row">
                    <input
                      autoComplete="off"
                      type="checkbox"
                      checked={chosen.includes(option.value)}
                      onChange={(e) =>
                        setChosen((current) =>
                          e.target.checked
                            ? [...current, option.value]
                            : current.filter((v) => v !== option.value),
                        )
                      }
                    />
                    {option.label}
                  </label>
                ))}
              </div>
            )}
            {(column.filter === "date" || column.filter === "number") && (
              <div className="column-menu-range">
                <label className="field">
                  From
                  <input
                    autoComplete="off"
                    type={column.filter}
                    aria-label={`${column.label} from`}
                    value={from}
                    onChange={(e) => setFrom(e.target.value)}
                  />
                </label>
                <label className="field">
                  To
                  <input
                    autoComplete="off"
                    type={column.filter}
                    aria-label={`${column.label} to`}
                    value={to}
                    onChange={(e) => setTo(e.target.value)}
                  />
                </label>
              </div>
            )}
            <div className="column-menu-actions">
              <button type="submit" className="button primary">
                Apply
              </button>
              {column.filter !== "select" && (
                <button
                  type="button"
                  className="button secondary"
                  onClick={() => apply({ empty: true })}
                >
                  Empty only
                </button>
              )}
              <button
                type="button"
                className="button secondary"
                disabled={!filter}
                onClick={() => apply(null)}
              >
                Clear
              </button>
            </div>
          </form>
        )}
      </PopoverSurface>
    </Popover>
  );
}
export function DataGrid<T extends { id: string }>({
  label,
  columns,
  rows,
  sort,
  direction,
  onSort,
  filters,
  onFilter,
  actions,
  actionsWidth = "150px",
  rowClassName,
  busy,
  empty,
}: {
  label: string;
  columns: GridColumn<T>[];
  rows: T[];
  sort?: string;
  direction?: string;
  onSort?: (key: string, direction: "asc" | "desc") => void;
  filters?: ColumnFilters;
  onFilter?: (key: string, value: ColumnFilter | null) => void;
  actions?: (row: T) => ReactNode;
  actionsWidth?: string;
  rowClassName?: (row: T) => string | undefined;
  busy?: boolean;
  empty?: ReactNode;
}) {
  const activeFilters = Object.entries(filters || {});
  // Columns the stylesheet hides on this screen width do not count towards the grid's minimum width.
  const viewport = useViewportWidth();
  const shown = columns.filter(
    (column) => !column.hideBelow || viewport >= column.hideBelow,
  );
  return (
    <>
      {activeFilters.length > 0 && onFilter && (
        <div className="grid-filters">
          <ul className="filter-chips" aria-label="Active column filters">
            {activeFilters.map(([key, value]) => {
              const column = columns.find((c) => c.sortKey === key);
              const text = column ? describeFilter(column, value) : key;
              return (
                <li key={key}>
                  <button
                    type="button"
                    onClick={() => onFilter(key, null)}
                    aria-label={`Remove filter ${text}`}
                  >
                    {text}
                    <X size={13} />
                  </button>
                </li>
              );
            })}
          </ul>
          <button
            type="button"
            className="button ghost"
            onClick={() =>
              activeFilters.forEach(([key]) => onFilter(key, null))
            }
          >
            Clear filters
          </button>
        </div>
      )}
      <section className="panel table-panel">
        <div
          className="table-scroll"
          tabIndex={0}
          role="region"
          aria-label={label}
          aria-busy={busy || undefined}
        >
          <table
            className="data-grid"
            style={{
              minWidth: minimumWidth(shown, actions ? actionsWidth : undefined),
            }}
          >
            <thead>
              <tr>
                {columns.map((column) => {
                  const active = column.sortKey && column.sortKey === sort;
                  const next =
                    active && direction === "asc" ? "desc" : ("asc" as const);
                  return (
                    <th
                      key={column.key}
                      style={column.width ? { width: column.width } : undefined}
                      className={`${column.align === "right" ? "align-right" : ""}${filters?.[column.sortKey || ""] ? " filtered" : ""}${column.hideBelow ? ` hide-below-${column.hideBelow}` : ""}`}
                      aria-sort={
                        active
                          ? direction === "asc"
                            ? "ascending"
                            : "descending"
                          : undefined
                      }
                    >
                      <span className="column-head">
                        {column.sortKey && onSort ? (
                          <button
                            type="button"
                            className="sort-header"
                            onClick={() =>
                              onSort(column.sortKey as string, next)
                            }
                          >
                            {column.label}
                            {active &&
                              (direction === "asc" ? (
                                <ArrowUp size={13} />
                              ) : (
                                <ArrowDown size={13} />
                              ))}
                          </button>
                        ) : (
                          column.label
                        )}
                        {column.sortKey && (onSort || onFilter) && (
                          <ColumnMenu
                            column={column}
                            sort={sort}
                            direction={direction}
                            filter={filters?.[column.sortKey]}
                            onSort={onSort}
                            onFilter={onFilter}
                          />
                        )}
                      </span>
                    </th>
                  );
                })}
                {actions && (
                  <th className="align-right" style={{ width: actionsWidth }}>
                    <span className="sr-only">Actions</span>
                  </th>
                )}
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <tr key={row.id} className={rowClassName?.(row)}>
                  {columns.map((column) => (
                    <td
                      key={column.key}
                      className={`${column.align === "right" ? "align-right" : ""}${column.hideBelow ? ` hide-below-${column.hideBelow}` : ""}`}
                    >
                      {column.render(row)}
                    </td>
                  ))}
                  {actions && (
                    <td className="align-right row-actions">{actions(row)}</td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
          {!rows.length && empty}
        </div>
      </section>
    </>
  );
}
/** In-memory sort + filter for grids whose rows are already loaded. */
export function localQuery<T>(
  rows: T[],
  columns: GridColumn<T>[],
  sort: string | undefined,
  direction: string | undefined,
  filters: ColumnFilters,
) {
  const accessor = (key: string) =>
    columns.find((c) => c.sortKey === key)?.value;
  let result = rows.filter((row) =>
    Object.entries(filters).every(([key, filter]) => {
      const read = accessor(key);
      if (!read) return true;
      const raw = read(row);
      const text = raw === null || raw === undefined ? "" : String(raw);
      if (typeof filter === "string")
        return text.toLowerCase().includes(filter.toLowerCase());
      if (Array.isArray(filter)) return filter.includes(text);
      if ("empty" in filter) return text === "";
      const value = typeof raw === "number" ? raw : text;
      if (filter.from !== undefined && filter.from !== "") {
        const from =
          typeof raw === "number" ? Number(filter.from) : filter.from;
        if (value < from) return false;
      }
      if (filter.to !== undefined && filter.to !== "") {
        const to =
          typeof raw === "number"
            ? Number(filter.to)
            : filter.to.length === 10
              ? `${filter.to}~`
              : filter.to;
        if (value > to) return false;
      }
      return true;
    }),
  );
  const read = sort ? accessor(sort) : undefined;
  if (read) {
    const factor = direction === "desc" ? -1 : 1;
    result = [...result].sort((a, b) => {
      const va = read(a) ?? "";
      const vb = read(b) ?? "";
      if (typeof va === "number" && typeof vb === "number")
        return (va - vb) * factor;
      return String(va).localeCompare(String(vb)) * factor;
    });
  }
  return result;
}
export function StatusPill({
  tone = "neutral",
  children,
}: {
  tone?: "neutral" | "green" | "blue" | "amber" | "rose" | "purple";
  children: ReactNode;
}) {
  // Sentence case for states ("On hold", "Credit note"): only the first letter is raised.
  const text =
    typeof children === "string"
      ? children.charAt(0).toUpperCase() + children.slice(1)
      : children;
  return <span className={`badge ${tone}`}>{text}</span>;
}

/** Width the columns need: pixel widths as given, others at a readable default. */
function minimumWidth<T>(
  columns: GridColumn<T>[],
  actionsWidth?: string,
): number {
  const px = (width: number | string | undefined, fallback: number) =>
    typeof width === "number"
      ? width
      : width?.endsWith("px")
        ? Number.parseFloat(width)
        : fallback;
  return (
    columns.reduce((sum, column) => sum + px(column.width, 160), 0) +
    (actionsWidth ? px(actionsWidth, 160) : 0)
  );
}

/** The window width, kept current; wide before the first measurement so nothing is dropped on the server render. */
function useViewportWidth(): number {
  const [width, setWidth] = useState(10000);
  useEffect(() => {
    const update = () => setWidth(window.innerWidth);
    update();
    window.addEventListener("resize", update);
    return () => window.removeEventListener("resize", update);
  }, []);
  return width;
}
