"use client";
import { useRef, useState } from "react";
import { FileKey2, LoaderCircle } from "lucide-react";
import { api } from "@/lib/api";
import { Notice } from "./notice";

/**
 * The licence file (.lic) as a drop zone with a button: drag the file in or choose it. Hive BDC checks that it was
 * issued for this tenant and keeps it with the settings (POST /license).
 */
export function LicenceUpload({ onStored, label = "Licence file" }: { onStored: () => void; label?: string }) {
  const input = useRef<HTMLInputElement>(null);
  const [over, setOver] = useState(false);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<{ ok: boolean; text: string } | null>(null);

  const send = async (file: File | undefined) => {
    if (!file) return;
    setBusy(true);
    setResult(null);
    try {
      const answer = await api("/license", { method: "POST", body: JSON.stringify({ content: await file.text(), fileName: file.name }) });
      setResult({ ok: true, text: answer?.message || "The licence is stored." });
      onStored();
    } catch (error) {
      setResult({ ok: false, text: (error as Error).message });
    } finally {
      setBusy(false);
      if (input.current) input.current.value = "";
    }
  };

  return (
    <div className="licence-upload">
      <div
        className={`drop-zone${over ? " over" : ""}`}
        onDragOver={(e) => {
          e.preventDefault();
          setOver(true);
        }}
        onDragLeave={() => setOver(false)}
        onDrop={(e) => {
          e.preventDefault();
          setOver(false);
          void send(e.dataTransfer.files[0]);
        }}
      >
        {busy ? <LoaderCircle className="spin" size={18} strokeWidth={1.75} /> : <FileKey2 size={18} strokeWidth={1.75} />}
        <span>{busy ? "Checking the licence" : "Drop the .lic file here or"}</span>
        <button type="button" className="button secondary" disabled={busy} onClick={() => input.current?.click()}>
          Choose file
        </button>
        <input
          ref={input}
          type="file"
          accept=".lic"
          aria-label={label}
          className="sr-only"
          onChange={(e) => void send(e.target.files?.[0])}
        />
      </div>
      {result && <Notice kind={result.ok ? "ok" : "error"}>{result.text}</Notice>}
    </div>
  );
}
