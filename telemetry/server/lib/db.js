import { neon } from "@neondatabase/serverless";

let sql;
export function db() {
  if (!sql) {
    if (!process.env.DATABASE_URL) throw new Error("DATABASE_URL ausente");
    sql = neon(process.env.DATABASE_URL);
  }
  return sql;
}
