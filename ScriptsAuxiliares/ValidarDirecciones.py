import pyodbc
import requests
import json

# Configuración de la conexión a la base de datos
server = 'ec2-34-192-211-16.compute-1.amazonaws.com'
database = 'ControlFleteros'
username = 'esa_local'
password = 'Kncb.0405'
driver = '{ODBC Driver 17 for SQL Server}'
connection_string = f'DRIVER={driver};SERVER={server};DATABASE={database};UID={username};PWD={password}'

# Conectar a la base de datos
conn = pyodbc.connect(connection_string)
cursor = conn.cursor()

# Consulta para obtener las direcciones y códigos postales
query = """
SELECT IdCabecera, direccion, LocalidadNombre
FROM cabeceraTemp
"""
cursor.execute(query)

# Procesar cada fila
rows = cursor.fetchall()
for row in rows:
    id_cabecera = row.IdCabecera
    direccion = row.direccion
    codigo_postal = row.LocalidadNombre

    # Construir la URL de la API
    url = f'https://apis.datos.gob.ar/georef/api/v2.0/direcciones?direccion={direccion}&codigo_postal={codigo_postal}'

    # Realizar la llamada HTTP a la API
    response = requests.get(url)

    # Verificar el estado de la respuesta
    if response.status_code != 200:
        print(f'Error en la llamada a la API: {response.text}')
        continue

    # Parsear la respuesta JSON
    data = response.json()
    if 'direcciones' in data and len(data['direcciones']) > 0:
        direccion_normalizada = data['direcciones'][0].get('nomenclatura', '')
        lat = data['direcciones'][0].get('ubicacion', {}).get('lat', '')
        lng = data['direcciones'][0].get('ubicacion', {}).get('lon', '')
        provincia_normalizada = data['direcciones'][0].get('provincia', {}).get('nombre', '')
        localidad_normalizada = data['direcciones'][0].get('localidad_censal', {}).get('nombre', '')

        # Actualizar la tabla cabeceraTemp con los resultados
        update_query = """
        UPDATE cabeceraTemp
        SET DireccionNormalizada = ?,
            Lat = ?,
            Lng = ?,
            ProvinciaNormalizada = ?,
            LocalidadNormalizada = ?
        WHERE IdCabecera = ?
        """
        cursor.execute(update_query, (direccion_normalizada, lat, lng, provincia_normalizada, localidad_normalizada, id_cabecera))
        conn.commit()

# Cerrar la conexión a la base de datos
cursor.close()
conn.close()