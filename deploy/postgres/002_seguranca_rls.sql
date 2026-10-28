-- =====================================================================================
-- Camada adicional de isolamento (defesa em profundidade) para PostgreSQL.
-- Aplicar DEPOIS de 001_inicial.sql, com um usuário administrador do banco.
--
-- STATUS: aplicado a cada execução da suíte PostgreSQL (dev/test-pg.ps1) e no banco de desenvolvimento
-- (dev/run-api.ps1). A API conecta como app_comercial: sem superusuário, sem CREATEROLE/CREATEDB, sem
-- BYPASSRLS, sem DDL e sem DELETE fora das tabelas listadas abaixo.
--
-- Modelo:
--  * A aplicação conecta como app_comercial (NÃO é dono das tabelas -> RLS se aplica).
--  * A cada conexão a API grava app.tenant_id e app.platform_scope (RlsConnectionInterceptor).
--  * Sem tenant definido e sem escopo de plataforma, nenhuma linha é visível (falha fechada).
-- =====================================================================================

SET client_min_messages = warning; -- oculta avisos de DROP ... IF EXISTS na primeira aplicação

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_comercial') THEN
    CREATE ROLE app_comercial LOGIN; -- defina a senha via cofre de segredos: ALTER ROLE app_comercial PASSWORD '...';
  END IF;
EXCEPTION WHEN duplicate_object OR unique_violation THEN
  NULL; -- criado em paralelo por outra sessão (papéis são globais no cluster)
END $$;

GRANT USAGE ON SCHEMA public TO app_comercial;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA public TO app_comercial;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO app_comercial;
-- DELETE só onde a aplicação realmente remove linhas (vínculos de papéis/permissões).
GRANT DELETE ON user_roles, user_permissions, push_tokens, product_barcodes, product_components, quote_items TO app_comercial;

CREATE OR REPLACE FUNCTION app_tenant_ok(t uuid) RETURNS boolean
LANGUAGE sql STABLE AS $$
  -- nullif antes do cast: o PostgreSQL não garante curto-circuito em AND/OR, então ''::uuid
  -- quebraria a consulta. Sem tenant, a comparação vira NULL (= nenhuma linha visível).
  SELECT coalesce(current_setting('app.platform_scope', true) = 'on', false)
      OR coalesce(t = nullif(current_setting('app.tenant_id', true), '')::uuid, false)
$$;

DO $$
DECLARE tbl text;
BEGIN
  FOREACH tbl IN ARRAY ARRAY[
    'stores','users','user_roles','user_permissions','user_sessions','devices','subscriptions','tenant_modules',
    'invoices','tenant_branding','branding_versions','branding_assets','categories','products','stock_movements',
    'stock_balances','sales','sale_items','payments','cash_sessions','cash_movements','store_hours',
    'operating_events','fiscal_documents','fiscal_events','sync_events','support_sessions','support_elevation_requests',
    'notifications','push_tokens','suppliers','purchase_orders','purchase_items','purchase_receipts',
    'customers','loyalty_programs','loyalty_transactions','financial_titles','sale_returns','sale_return_items','tenant_storage_settings','discount_authorizations','password_reset_tokens','tenant_payment_settings','pos_pix_charges','tenant_fiscal_profiles','fiscal_number_voids','product_barcodes','product_components','promotions','quotes','quote_items','product_batches','stock_transfers','stock_transfer_items','imported_invoices',
    'fiscal_xml_files','fiscal_exports','fiscal_month_closings','accountant_settings','accountant_deliveries']
  LOOP
    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', tbl);
    EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', tbl);
    EXECUTE format('DROP POLICY IF EXISTS tenant_isolation ON %I', tbl);
    EXECUTE format('CREATE POLICY tenant_isolation ON %I USING (app_tenant_ok("TenantId")) WITH CHECK (app_tenant_ok("TenantId"))', tbl);
  END LOOP;
END $$;

-- Auditoria: append-only também no banco (a aplicação já recusa UPDATE/DELETE).
REVOKE UPDATE, DELETE, TRUNCATE ON audit_logs FROM app_comercial;
CREATE OR REPLACE FUNCTION audit_logs_block_changes() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'audit_logs é somente inserção';
END $$;
DROP TRIGGER IF EXISTS trg_audit_logs_immutable ON audit_logs;
CREATE TRIGGER trg_audit_logs_immutable BEFORE UPDATE OR DELETE ON audit_logs
  FOR EACH ROW EXECUTE FUNCTION audit_logs_block_changes();

-- Movimentações financeiras e de estoque: sem UPDATE/DELETE para a aplicação.
REVOKE UPDATE, DELETE ON stock_movements, cash_movements, sale_items, payments, operating_events,
  fiscal_events, sync_events, payment_events, branding_versions,
  loyalty_transactions, purchase_receipts, sale_returns, sale_return_items, fiscal_xml_files FROM app_comercial;
