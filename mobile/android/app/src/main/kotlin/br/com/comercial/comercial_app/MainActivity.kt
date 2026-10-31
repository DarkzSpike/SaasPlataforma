package br.com.comercial.comercial_app

import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings
import androidx.core.content.FileProvider
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel
import java.io.File

/// Atualização do app: o Dart baixa o APK para a pasta de cache e este canal abre o instalador do Android,
/// que sempre pede a confirmação do usuário. Só instala por cima se o APK tiver a mesma assinatura.
class MainActivity : FlutterActivity() {
    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, "comercial/updater").setMethodCallHandler { call, result ->
            when (call.method) {
                "updatesDir" -> result.success(File(cacheDir, "updates").apply { mkdirs() }.absolutePath)
                "canInstall" -> result.success(Build.VERSION.SDK_INT < Build.VERSION_CODES.O || packageManager.canRequestPackageInstalls())
                "openInstallSettings" -> {
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O)
                        startActivity(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:$packageName")))
                    result.success(null)
                }
                "install" -> {
                    val file = File(call.argument<String>("path")!!)
                    val updates = File(cacheDir, "updates").canonicalPath
                    if (!file.canonicalPath.startsWith(updates) || !file.exists()) {
                        result.error("invalid_file", "Arquivo de atualização não encontrado.", null)
                    } else {
                        val uri = FileProvider.getUriForFile(this, "$packageName.updates", file)
                        startActivity(Intent(Intent.ACTION_VIEW).apply {
                            setDataAndType(uri, "application/vnd.android.package-archive")
                            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
                        })
                        result.success(null)
                    }
                }
                else -> result.notImplemented()
            }
        }
    }
}
