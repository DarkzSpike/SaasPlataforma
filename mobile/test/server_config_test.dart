import 'package:comercial_app/core/server_config.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('endereço digitado sem http vira http e perde a barra final', () {
    expect(ServerConfig.normalize('192.168.100.176:5080/'), 'http://192.168.100.176:5080');
    expect(ServerConfig.normalize(' https://api.loja.com.br '), 'https://api.loja.com.br');
  });

  test('HTTP só na rede local; na internet só HTTPS', () {
    expect(ServerConfig.isAllowed(Uri.parse('http://192.168.100.176:5080/x')), isTrue);
    expect(ServerConfig.isAllowed(Uri.parse('http://10.0.2.2:5080/x')), isTrue);
    expect(ServerConfig.isAllowed(Uri.parse('http://172.20.1.1/x')), isTrue);
    expect(ServerConfig.isAllowed(Uri.parse('http://172.40.1.1/x')), isFalse);
    expect(ServerConfig.isAllowed(Uri.parse('http://api.loja.com.br/x')), isFalse);
    expect(ServerConfig.isAllowed(Uri.parse('https://api.loja.com.br/x')), isTrue);
  });
}
