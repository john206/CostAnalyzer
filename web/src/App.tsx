import { useMemo, useState } from "react";

// Uses .env VITE_API_URL if present; otherwise defaults to https://localhost:8443
const apiBase =
    (import.meta as any).env?.VITE_API_URL ?? "https://localhost:8443";

/** Exact API response shape (PascalCase) */
type UiResult = {
    LandedCostUsd: number;
    LandedCostCop: number;
    UnitCostCop: number;
    UnitGrossProfitCop: number;
    UnitGrossMarginPercent: number; // 0-100 already
    BreakEvenPriceCop: number;
};

type FormState = {
    UnitCount: string;
    DeclaredUnitValueUsd: string;
    FreightCostUsd: string;
    InsuranceRatePercent: string; // 0.003 = 0.3%
    OriginChargesUsd: string;
    DestinationChargesUsd: string;
    CustomsBrokerUsd: string;

    DutyRatePercent: string;             // 0.10 = 10%
    ValueAddedTaxRatePercent: string;    // 0.19 = 19%
    OtherTaxesRatePercent: string;       // 0.00 = 0%
    BankForeignExchangeSpreadPercent: string; // 0.01 = 1%
    PaymentFeePercent: string;           // 0.009 = 0.9%

    UsdToCopRate: string;

    SalePriceCop: string;
    CommissionPercent: string;           // 0.14 = 14%
    PaymentGatewayPercent: string;       // 0.029 = 2.9%
    FulfillmentFeeCop: string;
    LastMileCop: string;
    MiscellaneousAdminCostCop: string;

    IsCifShipment: boolean;
};

const defaults: FormState = {
    UnitCount: "500",
    DeclaredUnitValueUsd: "6",
    FreightCostUsd: "1200",
    InsuranceRatePercent: "0.003",
    OriginChargesUsd: "0",
    DestinationChargesUsd: "300",
    CustomsBrokerUsd: "120",

    DutyRatePercent: "0.10",
    ValueAddedTaxRatePercent: "0.19",
    OtherTaxesRatePercent: "0.00",
    BankForeignExchangeSpreadPercent: "0.01",
    PaymentFeePercent: "0.009",

    UsdToCopRate: "4000",

    SalePriceCop: "69900",
    CommissionPercent: "0.14",
    PaymentGatewayPercent: "0.029",
    FulfillmentFeeCop: "1200",
    LastMileCop: "300000",
    MiscellaneousAdminCostCop: "200000",

    IsCifShipment: false
};

const num = (v: string) => Number(v.trim() || "0");
const isNum = (v: string) => v.trim() !== "" && !Number.isNaN(Number(v));
const format = (n: unknown, fixed2 = false) => {
    if (typeof n !== "number" || !isFinite(n)) return "-";
    return fixed2 ? n.toFixed(2) : n.toLocaleString();
};

// Narrow the parsed JSON to UiResult (PascalCase only)
function isUiResult(x: any): x is UiResult {
    return (
        x &&
        ["LandedCostUsd", "LandedCostCop", "UnitCostCop", "UnitGrossProfitCop", "UnitGrossMarginPercent", "BreakEvenPriceCop"]
            .every(k => typeof x[k] === "number" && isFinite(x[k]))
    );
}

export default function App() {
    const [f, setF] = useState<FormState>(defaults);
    const [loading, setLoading] = useState(false);
    const [result, setResult] = useState<UiResult | null>(null);
    const [error, setError] = useState<string | null>(null);

    const invalids = useMemo(() => {
        const required = ["UnitCount", "DeclaredUnitValueUsd", "UsdToCopRate", "SalePriceCop"] as const;
        return required.filter((k) => !isNum(f[k]));
    }, [f]);

    async function calculate() {
        setLoading(true);
        setError(null);
        setResult(null);

        const body = {
            UnitCount: num(f.UnitCount),
            DeclaredUnitValueUsd: num(f.DeclaredUnitValueUsd),
            FreightCostUsd: num(f.FreightCostUsd),
            InsuranceRatePercent: num(f.InsuranceRatePercent),
            OriginChargesUsd: num(f.OriginChargesUsd),
            DestinationChargesUsd: num(f.DestinationChargesUsd),
            CustomsBrokerUsd: num(f.CustomsBrokerUsd),

            DutyRatePercent: num(f.DutyRatePercent),
            ValueAddedTaxRatePercent: num(f.ValueAddedTaxRatePercent),
            OtherTaxesRatePercent: num(f.OtherTaxesRatePercent),
            BankForeignExchangeSpreadPercent: num(f.BankForeignExchangeSpreadPercent),
            PaymentFeePercent: num(f.PaymentFeePercent),

            UsdToCopRate: num(f.UsdToCopRate),

            SalePriceCop: num(f.SalePriceCop),
            CommissionPercent: num(f.CommissionPercent),
            PaymentGatewayPercent: num(f.PaymentGatewayPercent),
            FulfillmentFeeCop: num(f.FulfillmentFeeCop),
            LastMileCop: num(f.LastMileCop),
            MiscellaneousAdminCostCop: num(f.MiscellaneousAdminCostCop),

            IsCifShipment: f.IsCifShipment
        };

        try {
            const res = await fetch(`${apiBase}/calculate`, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(body)
            });
            if (!res.ok) {
                const text = await res.text();
                throw new Error(`${res.status} ${res.statusText}: ${text}`);
            }
            const data = await res.json();
            if (!isUiResult(data)) throw new Error("Unexpected response shape from API (PascalCase required).");
            setResult(data);
        } catch (e: any) {
            console.error("Calculate error:", e);
            setError(e?.message ?? "Request failed");
        } finally {
            setLoading(false);
        }
    }

    const set = (k: keyof FormState) => (e: any) => {
        const v = typeof e === "boolean" ? e : e.target?.type === "checkbox" ? e.target.checked : e.target.value;
        setF((s) => ({ ...s, [k]: v }));
    };

    return (
        <div className="container">
            <h1>Cost Analyzer</h1>
            <p>API base: <code>{apiBase}</code></p>

            <section className="grid">
                <Field label="Units" value={f.UnitCount} onChange={set("UnitCount")} />
                <Field label="Declared value per unit (USD)" value={f.DeclaredUnitValueUsd} onChange={set("DeclaredUnitValueUsd")} />

                <Field label="Freight cost (USD)" value={f.FreightCostUsd} onChange={set("FreightCostUsd")} />
                <Field label="Insurance rate (fraction)" hint="0.003 = 0.3%" value={f.InsuranceRatePercent} onChange={set("InsuranceRatePercent")} />

                <Field label="Origin charges (USD)" value={f.OriginChargesUsd} onChange={set("OriginChargesUsd")} />
                <Field label="Destination charges (USD)" value={f.DestinationChargesUsd} onChange={set("DestinationChargesUsd")} />
                <Field label="Customs broker (USD)" value={f.CustomsBrokerUsd} onChange={set("CustomsBrokerUsd")} />

                <Field label="Duty rate (fraction)" hint="0.10 = 10%" value={f.DutyRatePercent} onChange={set("DutyRatePercent")} />
                <Field label="VAT rate (fraction)" hint="0.19 = 19%" value={f.ValueAddedTaxRatePercent} onChange={set("ValueAddedTaxRatePercent")} />
                <Field label="Other taxes (fraction)" value={f.OtherTaxesRatePercent} onChange={set("OtherTaxesRatePercent")} />

                <Field label="FX bank spread (fraction)" hint="0.01 = 1%" value={f.BankForeignExchangeSpreadPercent} onChange={set("BankForeignExchangeSpreadPercent")} />
                <Field label="Payment fee (fraction)" hint="0.009 = 0.9%" value={f.PaymentFeePercent} onChange={set("PaymentFeePercent")} />

                <Field label="USD -> COP rate" value={f.UsdToCopRate} onChange={set("UsdToCopRate")} />

                <Field label="Sale price (COP)" value={f.SalePriceCop} onChange={set("SalePriceCop")} />
                <Field label="Channel commission (fraction)" hint="0.14 = 14%" value={f.CommissionPercent} onChange={set("CommissionPercent")} />
                <Field label="Payment gateway (fraction)" hint="0.029 = 2.9%" value={f.PaymentGatewayPercent} onChange={set("PaymentGatewayPercent")} />
                <Field label="Fulfillment fee (COP)" value={f.FulfillmentFeeCop} onChange={set("FulfillmentFeeCop")} />
                <Field label="Last-mile cost (COP)" value={f.LastMileCop} onChange={set("LastMileCop")} />
                <Field label="Misc admin cost (COP)" value={f.MiscellaneousAdminCostCop} onChange={set("MiscellaneousAdminCostCop")} />
            </section>

            <label className="checkbox-row">
                <input type="checkbox" checked={f.IsCifShipment} onChange={(e) => set("IsCifShipment")(e)} />
                Treat shipment as CIF (freight and insurance included)
            </label>

            {invalids.length > 0 && (
                <div className="error">
                    Missing or invalid: {invalids.join(", ")}
                </div>
            )}

            <div style={{ marginTop: 12 }}>
                <button
                    onClick={calculate}
                    disabled={loading || invalids.length > 0}
                    className="button"
                >
                    {loading ? "Calculating..." : "Calculate"}
                </button>
            </div>

            {error && (
                <div className="error">
                    <strong>Error: </strong>{error}
                </div>
            )}

            {result && (
                <div className="grid" style={{ marginTop: 16 }}>
                    <Tile label="Landed USD" value={result.LandedCostUsd} />
                    <Tile label="Landed COP" value={result.LandedCostCop} />
                    <Tile label="Unit Cost COP" value={result.UnitCostCop} />
                    <Tile label="Unit Profit COP" value={result.UnitGrossProfitCop} />
                    <Tile label="Unit Margin %" value={result.UnitGrossMarginPercent} fixed2 suffix="%" />
                    <Tile label="Break-Even Price COP" value={result.BreakEvenPriceCop} />
                </div>
            )}
        </div>
    );
}

function Field({
    label,
    value,
    onChange,
    hint
}: {
    label: string;
    value: string;
    onChange: (e: any) => void;
    hint?: string;
}) {
    return (
        <div>
            <label className="label">{label}</label>
            <input
                className="input"
                value={value}
                onChange={onChange}
                inputMode="decimal"
            />
            {hint && <div className="label" style={{ marginTop: 4 }}>{hint}</div>}
        </div>
    );
}

function Tile({
    label,
    value,
    suffix,
    fixed2
}: {
    label: string;
    value: number;
    suffix?: string;
    fixed2?: boolean;
}) {
    return (
        <div className="tile">
            <div className="title">{label}</div>
            <div className="value">
                {format(value, !!fixed2)}{suffix ? ` ${suffix}` : ""}
            </div>
        </div>
    );
}
