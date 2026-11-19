import Link from "next/link";

export default function NotFound() {
  return (
    <div className="empty-state">
      <h1>This page does not exist</h1>
      <p>The address may be from an older version of Hive BDC.</p>
      <Link className="button" href="/">
        Back to the overview
      </Link>
    </div>
  );
}
